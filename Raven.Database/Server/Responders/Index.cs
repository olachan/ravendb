using System;
using System.Collections.Generic;
using Raven.Database.Data;
using Raven.Database.Indexing;
using System.Linq;
using Raven.Database.Extensions;
using Raven.Database.Queries;
using Raven.Http.Abstractions;
using Raven.Http.Extensions;

namespace Raven.Database.Server.Responders
{
	public class Index : RequestResponder
	{
		public override string UrlPattern
		{
			get { return @"^/indexes/(.+)"; }
		}

		public override string[] SupportedVerbs
		{
			get { return new[] {"GET", "PUT", "DELETE","HEAD","RESET"}; }
		}

		public override void Respond(IHttpContext context)
		{
			var match = urlMatcher.Match(context.GetRequestUrl());
			var index = match.Groups[1].Value;

			switch (context.Request.HttpMethod)
			{
				case "HEAD":
					if(Database.IndexDefinitionStorage.IndexNames.Contains(index, StringComparer.InvariantCultureIgnoreCase) == false)
						context.SetStatusToNotFound();
					break;
				case "GET":
					OnGet(context, index);
					break;
				case "PUT":
					Put(context, index);
					break;
				case "RESET":
					Database.ResetIndex(index);
					context.WriteJson(new {Reset = index});
					break;
				case "DELETE":
					if(index.StartsWith("Raven/",StringComparison.InvariantCultureIgnoreCase))
					{
						context.SetStatusToForbidden();
						context.WriteJson(new
						{
							Url = context.Request.RawUrl,
							Error = "Builtin indexes cannot be deleted, attempt to delete index '" + index + "' was rejected"
						});
						return;
					}
					context.SetStatusToDeleted();
					Database.DeleteIndex(index);
					break;
			}
		}

		private void Put(IHttpContext context, string index)
		{
			if (BuiltinIndex(index, context))
				return;
			var data = context.ReadJsonObject<IndexDefinition>();
			if (data.Map == null)
			{
				context.SetStatusToBadRequest();
				context.Write("Expected json document with 'Map' property");
				return;
			}
			context.SetStatusToCreated("/indexes/" + index);
			context.WriteJson(new { Index = Database.PutIndex(index, data) });
		}

		private static bool BuiltinIndex(string index, IHttpContext context)
		{
			if (!index.StartsWith("Raven/", StringComparison.InvariantCultureIgnoreCase))
				return false;

			context.SetStatusToForbidden();
			context.WriteJson(new
			{
				Url = context.Request.RawUrl,
				Error = "Builtin indexes cannot be modified, attempt to modifiy index '" + index + "' was rejected"
			});
			return true;
		}

		private void OnGet(IHttpContext context, string index)
		{
			var definition = context.Request.QueryString["definition"];
			if ("yes".Equals(definition, StringComparison.InvariantCultureIgnoreCase))
			{
				var indexDefinition = Database.GetIndexDefinition(index);
				if(indexDefinition == null)
				{
					context.SetStatusToNotFound();
					return;
				}
				context.WriteJson(new {Index = indexDefinition});
			}
			else
			{
				var queryResult = ExecuteQuery(context, index);

				if (queryResult == null)
					return;

				var includes = context.Request.QueryString.GetValues("include") ?? new string[0];
				var loadedIds = new HashSet<string>(
					queryResult.Results
						.Where(x => x["@metadata"] != null)
						.Select(x => x["@metadata"].Value<string>("@id"))
						.Where(x => x != null)
					);
                var command = new AddIncludesCommand(Database, GetRequestTransaction(context), (etag, doc) => queryResult.Includes.Add(doc), includes, loadedIds);
				foreach (var result in queryResult.Results)
				{
					command.Execute(result);
				}
				context.Response.Headers["ETag"] = queryResult.IndexEtag.ToString();
				context.WriteJson(queryResult);
			}
		}

		private QueryResult ExecuteQuery(IHttpContext context, string index)
		{
			var indexQuery = context.GetIndexQueryFromHttpContext(Database.Configuration.MaxPageSize);

			return index.StartsWith("dynamic", StringComparison.InvariantCultureIgnoreCase) ? 
				PerformQueryAgainstDynamicIndex(context, index, indexQuery) : 
				PerformQueryAgainstExistingIndex(context, index, indexQuery);
		}

		private QueryResult PerformQueryAgainstExistingIndex(IHttpContext context, string index, IndexQuery indexQuery)
		{
		    var indexEtag = GetIndexEtag(index);
		    if (context.MatchEtag(indexEtag))
			{
				context.SetStatusToNotModified();
				return null;
			}

		    var queryResult = Database.Query(index, indexQuery);
            queryResult.IndexEtag = indexEtag;
		    return queryResult;
		}

		private QueryResult PerformQueryAgainstDynamicIndex(IHttpContext context, string index, IndexQuery indexQuery)
		{
			string entityName = null;
			if (index.StartsWith("dynamic/"))
				entityName = index.Substring("dynamic/".Length);

			var dynamicIndexName = Database.FindDynamicIndexName(entityName, indexQuery.Query);
		    var indexEtag = Guid.Empty;
            if (Database.IndexStorage.HasIndex(dynamicIndexName))
			{
			    indexEtag = GetIndexEtag(dynamicIndexName);
			    if (context.MatchEtag(indexEtag))
				{
					context.SetStatusToNotModified();
					return null;
				}
			}

            var queryResult = Database.ExecuteDynamicQuery(entityName, indexQuery, context.GetAggregationOperation());
            if(indexEtag != Guid.Empty)
		        queryResult.IndexEtag = indexEtag;
		    return queryResult;
		}

	    private Guid GetIndexEtag(string indexName)
	    {
	        Guid lastDocEtag = Guid.Empty;
	        Tuple<DateTime, Guid> indexLastUpdatedAt = null;
	        Database.TransactionalStorage.Batch(accessor =>
	        {
	            lastDocEtag = accessor.Staleness.GetMostRecentDocumentEtag();
	            indexLastUpdatedAt = accessor.Staleness.IndexLastUpdatedAt(indexName);
	        });
	        var etagBytes = lastDocEtag.ToByteArray();
	        var lastIndexedEtagBytes = indexLastUpdatedAt.Item2.ToByteArray();
	        for (int i = 0; i < 16; i++)
	        {
	            etagBytes[i] ^= lastIndexedEtagBytes[i];
	        }

	        return new Guid(etagBytes);
	    }
	}
}
