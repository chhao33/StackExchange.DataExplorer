﻿using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Web.Mvc;
using StackExchange.DataExplorer.Helpers;
using StackExchange.DataExplorer.Models;
using Newtonsoft.Json;

namespace StackExchange.DataExplorer.Controllers
{
    public class QueryController : StackOverflowController
    {

        [Route(@"query/job/{guid}")]
        public ActionResult PollJob(Guid guid)
        {
            var result = AsyncQueryRunner.PollJob(guid);
            if (result == null)
            {
                return Json(new {error = "unknown job being polled!" });
            }

            if (result.State == AsyncQueryRunner.AsyncState.Failure)
            {
                return TransformExecutionException(result.Exception);
            }

            if (result.State == AsyncQueryRunner.AsyncState.Pending)
            {
                return Json(new { running = true, job_id = result.JobId });
            }

            try
            {
                return CompleteResponse(result.QueryResults, result.ParsedQuery, result.TextResults, result.Parent, result.Title, result.Description, result.Site.Id);
            }
            catch (Exception ex)
            { 
                return TransformExecutionException(ex);
            }

        }

        [HttpPost]
        [Route(@"query/save/{siteId:\d+}/{parentId?:\d+}")]
        public ActionResult Create(string sql, string title, string description, int siteId, int? parentId, bool? textResults, bool? withExecutionPlan, bool? crossSite, bool? excludeMetas)
        {
            if (CurrentUser.IsAnonymous && !CaptchaController.CaptchaPassed(GetRemoteIP()))
            {
                return Json(new { captcha = true });
            }

            ActionResult response = null;
            try
            {
                Revision parent = null;

                if (parentId.HasValue)
                {
                    parent = QueryUtil.GetBasicRevision(parentId.Value);

                    if (parent == null)
                    {
                        throw new ApplicationException("Invalid revision ID");
                    }
                }

                var parsedQuery = new ParsedQuery(
                    sql,
                    Request.Params,
                    withExecutionPlan == true,
                    crossSite == true,
                    excludeMetas == true
                );

                QueryResults results = null;
                Site site = GetSite(siteId);
                ValidateQuery(parsedQuery, site);

                var asyncResults = AsyncQueryRunner.Execute(parsedQuery, CurrentUser, site, title, description, parent, textResults == true);

                if (asyncResults.State == AsyncQueryRunner.AsyncState.Failure)
                {
                    throw asyncResults.Exception; 
                }

                if (asyncResults.State == AsyncQueryRunner.AsyncState.Success)
                {
                    results = asyncResults.QueryResults;
                }
                else
                {
                    return Json(new {running = true, job_id = asyncResults.JobId});
                }

                response = CompleteResponse(results, parsedQuery, textResults == true, parent, title, description, siteId);
            }
            catch (Exception ex)
            {
                response = TransformExecutionException(ex);
            }

            return response;
        }

        private ActionResult CompleteResponse(
            QueryResults results, 
            ParsedQuery parsedQuery, 
            bool textResults, 
            Revision parent, 
            string title, 
            string description,
            int siteId
            )
        {
            results = TranslateResults(parsedQuery, textResults, results);

            var query = Current.DB.Query<Query>(
                "SELECT * FROM Queries WHERE QueryHash = @hash",
                new
                {
                    hash = parsedQuery.Hash
                }
            ).FirstOrDefault();

            int revisionId = 0, queryId;
            DateTime saveTime;

            // We only create revisions if something actually changed.
            // We'll log it as an execution anyway if applicable, so the user will
            // still get a link in their profile, just not their own revision.
            if (parent == null || query == null || query.Id != parent.QueryId)
            {
                if (query == null)
                {
                    queryId = (int)Current.DB.Queries.Insert(
                        new
                        {
                            QueryHash = parsedQuery.Hash,
                            QueryBody = parsedQuery.Sql
                        }
                    );
                }
                else
                {
                    queryId = query.Id;
                }

                revisionId = (int)Current.DB.Revisions.Insert(
                    new
                    {
                        QueryId = queryId,
                        OwnerId = CurrentUser.IsAnonymous ? null : (int?)CurrentUser.Id,
                        OwnerIP = GetRemoteIP(),
                        CreationDate = saveTime = DateTime.UtcNow
                    }
                );

                var revision = new Revision
                {
                    Id = revisionId,
                    QueryId = queryId
                };

                SaveMetadata(revision, title, description, true);

                results.RevisionId = revisionId;
                results.Created = saveTime;
            }
            else
            {
                queryId = query.Id;
                results.RevisionId = parent.Id;
            }

            if (parent != null)
            {
                results.ParentId = parent.Id;
            }

            if (title != null)
            {
                results.Slug = title.URLFriendly();
            }

            QueryRunner.LogRevisionExecution(CurrentUser, siteId, results.RevisionId);

            // Consider handling this XSS condition (?) in the ToJson() method instead, if possible
            return Content(results.ToJson().Replace("/", "\\/"), "application/json");
        }


        [HttpPost]
        [Route(@"query/run/{siteId:\d+}/{revisionId:\d+}")]
        public ActionResult Execute(int revisionId, int siteId, bool? textResults, bool? withExecutionPlan, bool? crossSite, bool? excludeMetas)
        {
            if (CurrentUser.IsAnonymous && !CaptchaController.CaptchaPassed(GetRemoteIP()))
            {
                return Json(new { captcha = true });
            }

            ActionResult response = null;

            try
            {
                var query = QueryUtil.GetQueryForRevision(revisionId);

                if (query == null)
                {
                    throw new ApplicationException("Invalid revision ID");
                }

                var parsedQuery = new ParsedQuery(
                    query.QueryBody,
                    Request.Params,
                    withExecutionPlan == true,
                    crossSite == true,
                    excludeMetas == true
                );

                var results = ExecuteWithResults(parsedQuery, siteId, textResults == true);

                // It might be bad that we have to do this here
                results.RevisionId = revisionId;

                QueryRunner.LogRevisionExecution(CurrentUser, siteId, revisionId);

                // Consider handling this XSS condition (?) in the ToJson() method instead, if possible
                response = Content(results.ToJson().Replace("/", "\\/"), "application/json");
            }
            catch (Exception ex)
            {
                response = TransformExecutionException(ex);
            }

            return response;
        }

        [HttpPost]
        [Route(@"query/update/{revisionId:\d+}")]
        public ActionResult UpdateMetadata(int revisionId, string title, string description)
        {
            ActionResult response = null;

            try
            {
                Revision revision = QueryUtil.GetBasicRevision(revisionId);

                if (revision == null)
                {
                    throw new ApplicationException("Invalid revision ID");
                }

                SaveMetadata(revision, title, description, false);
            }
            catch (ApplicationException ex)
            {
                response = TransformExecutionException(ex);
            }

            return response;
        }

        [Route(@"{sitename}/csv/{revisionId:\d+}/{slug?}", RoutePriority.Low)]
        public ActionResult ShowSingleSiteCsv(string sitename, int revisionId)
        {
            Query query = QueryUtil.GetQueryForRevision(revisionId);

            if (query == null)
            {
                return PageNotFound();
            }

            var site = GetSite(sitename);

            if (sitename == null)
            {
                return PageNotFound();
            }

            CachedResult cachedResults = QueryUtil.GetCachedResults(
                new ParsedQuery(query.QueryBody, Request.Params),
                Site.Id
            );
            List<ResultSet> resultSets;

            if (cachedResults != null)
            {
                resultSets = JsonConvert.DeserializeObject<List<ResultSet>>(cachedResults.Results);
            }
            else
            {
                resultSets = QueryRunner.GetResults(
                    new ParsedQuery(query.QueryBody, Request.Params),
                    site,
                    CurrentUser
                ).ResultSets;
            }

            return new CsvResult(resultSets);
        }

        [Route(@"{sitename}/mcsv/{revisionId:\d+}/{slug?}", RoutePriority.Low)]
        public ActionResult ShowMultiSiteCsv(string sitename, int revisionId)
        {
            Query query = QueryUtil.GetQueryForRevision(revisionId);

            if (query == null)
            {
                return PageNotFound();
            }

            var results = QueryRunner.GetResults(
                new ParsedQuery(query.QueryBody, Request.Params, crossSite: true, excludeMetas: false),
                null,
                CurrentUser
            );

            return new CsvResult(results.ResultSets);
        }

        [Route(@"{sitename}/nmcsv/{revisionId:\d+}/{slug?}", RoutePriority.Low)]
        public ActionResult ShowMultiSiteWithoutMetaCsv(string sitename, int revisionId)
        {
            Query query = QueryUtil.GetQueryForRevision(revisionId);

            if (query == null)
            {
                return PageNotFound();
            }

            var results = QueryRunner.GetResults(
                new ParsedQuery(query.QueryBody, Request.Params, crossSite: true, excludeMetas: true),
                null,
                CurrentUser
            );

            return new CsvResult(results.ResultSets);
        }

        [Route(@"{sitename}/q/{queryId:\d+}/{slug?}")]
        public ActionResult MapQuery(string sitename, int queryId, string slug)
        {
            Revision revision = QueryUtil.GetMigratedRevision(queryId, MigrationType.Normal);

            if (revision == null)
            {
                return PageNotFound();
            }

            if (slug.HasValue())
            {
                slug = "/" + slug;
            }

            return new RedirectPermanentResult("/" + sitename + "/query/" + revision.Id + slug);
        }

        [Route(@"{sitename}/query/edit/{querySetId:\d+}/{slug?}")]
        public ActionResult Edit(string sitename, int querySetId)
        {
            bool foundSite = SetCommonQueryViewData(sitename);

            if (!foundSite)
            {
                return PageNotFound();
            }

            SetHeader("Editing Query");

            Revision revision = QueryUtil.GetCompleteLatestRevision(querySetId);

            if (revision == null)
            {
                return PageNotFound();
            }

            ViewData["query_action"] = "save/" + Site.Id +  "/" + revision.Id;
            ViewData["revision"] = revision;

            if (!CurrentUser.IsAnonymous)
            {
                ViewData["history"] = QueryUtil.GetRevisionHistory(revision.QuerySet.Id);
            }

            return View("Editor", Site);
        }

        /// <summary>
        /// Download a query execution plan as xml.
        /// </summary>
        [Route(@"{sitename}/plan/{revisionId:\d+}/{slug?}", RoutePriority.Low)]
        public ActionResult ShowPlan(string sitename, int revisionId)
        {
            Query query = QueryUtil.GetQueryForRevision(revisionId);

            if (query == null)
            {
                return PageNotFound();
            }

            CachedResult cache = QueryUtil.GetCachedResults(
                new ParsedQuery(query.QueryBody, Request.Params),
                Site.Id
            );

            if (cache == null || cache.ExecutionPlan == null)
            {
                return PageNotFound();
            }

            return new QueryPlanResult(cache.ExecutionPlan);
        }

        [Route("{sitename}/query/new", RoutePriority.Low)]
        public ActionResult New(string sitename)
        {
            bool foundSite = SetCommonQueryViewData(sitename);

            if (!foundSite)
            {
                return PageNotFound();
            }

            ViewData["query_action"] = "save/" + Site.Id;

            return View("Editor", Site);
        }

        private QueryResults ExecuteWithResults(ParsedQuery query, int siteId, bool textResults)
        {
            QueryResults results = null;
            Site site = GetSite(siteId);
            ValidateQuery(query, site);

            results = QueryRunner.GetResults(query, site, CurrentUser);
            results = TranslateResults(query, textResults, results);
            return results;
        }

        private static QueryResults TranslateResults(ParsedQuery query, bool textResults, QueryResults results)
        {
            textResults = textResults || (results.ResultSets.Count != 1);
            if (!textResults) results.Messages = QueryResults.FormatTextResults("", results.ResultSets);

            if (textResults)
            {
                results = results.ToTextResults();
            }

            if (query.IncludeExecutionPlan)
            {
                results = results.TransformQueryPlan();
            }
            return results;
        }

        private static void ValidateQuery(ParsedQuery query, Site site)
        {
            if (!query.IsExecutionReady)
            {
                throw new ApplicationException(!string.IsNullOrEmpty(query.ErrorMessage) ?
                    query.ErrorMessage : "All parameters must be set!");
            }

            if (site == null)
            {
                throw new ApplicationException("Invalid site ID");
            }
        }

        private ActionResult TransformExecutionException(Exception ex)
        {
            var response = new Dictionary<string, string>();
            var sqlex = ex as SqlException;

            if (sqlex != null)
            {
                response["line"] = sqlex.LineNumber.ToString();
            }

            response["error"] = ex.Message;

            return Json(response);
        }

        private void SaveMetadata(Revision revision, string title, string description, bool updateWithoutChange)
        {
            QuerySet querySet = null;

            if (title.IsNullOrEmpty())
            {
                title = null;
            }

            if (description.IsNullOrEmpty())
            {
                description = null;
            }

            if (!CurrentUser.IsAnonymous)
            {
                querySet = Current.DB.Query<QuerySet>(@"
                    SELECT
                        *
                    FROM
                        QuerySets
                    WHERE
                        InitialRevisionId = @revision AND
                        OwnerId = @owner",
                    new
                    {
                        owner = CurrentUser.Id
                    }
                ).FirstOrDefault();
            }

            // We always save a querys set for anonymous users since they don't have an
            // actual revision history that we're associating the query set with
            if (CurrentUser.IsAnonymous || querySet == null)
            {
                Current.DB.QuerySets.Insert(
                    new
                    {
                        InitialRevisionId = revision.Id,
                        CurrentRevisionId = revision.Id,
                        OwnerId = CurrentUser.IsAnonymous ? (int?)null : CurrentUser.Id,
                        Title = title,
                        Description = description,
                        LastActivity = DateTime.UtcNow,
                        Votes = 0, 
                        Views = 0
                    }
                );
            }
            else if (querySet.Title != title || querySet.Description != description)
            {
                Current.DB.QuerySets.Update(querySet.Id,
                    new
                    {
                        Title = title,
                        Description = description,
                        CurrentRevisionId = revision.Id,
                        LastActivity = DateTime.UtcNow
                    }
                );
            }
        }

        private bool SetCommonQueryViewData(string sitename)
        {
            SetHeader("Viewing Query");
            var s = GetSite(sitename);
            if (s==null)
            {
                return false;
            }
            Site = s;
            SelectMenuItem("Compose Query");

            ViewData["GuessedUserId"] = Site.GuessUserId(CurrentUser);
            ViewData["Tables"] = Site.GetTableInfos();
            ViewData["Sites"] = Current.DB.Sites.All();

            return true;
        }
    }
}