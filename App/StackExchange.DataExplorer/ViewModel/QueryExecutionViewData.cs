﻿using System;
using System.Text;
using StackExchange.DataExplorer.Models;

namespace StackExchange.DataExplorer.ViewModel
{
    public struct QueryExecutionViewData
    {
        private string name;
        private QueryVoting voting;

        public QueryVoting QueryVoting
        {
            get
            {
                return voting ?? new QueryVoting
                {
                    HasVoted = false,
                    TotalVotes = FavoriteCount,
                    ReadOnly = true
                };
            }

            set { voting = value; }
        }

        public string Name
        {
            get
            {
                return name ?? Query.SqlAsTitle(SQL);
            }
            set
            {
                name = value.IsNullOrEmpty() ? null : value;
            }
        }

        public string Url
        {
            get
            {
                var slug = name == null ? "" : "/" + name.URLFriendly();

                if (RevisionId == null)
                {
                    return string.Format("/{0}/query/{1}{2}", SiteName, QuerySetId, slug);
                }
                else
                {
                    return string.Format("/{0}/revision/{1}/{2}{3}", SiteName, QuerySetId, RevisionId, slug);
                }
            }
        }

        public string URLWithStub(string stub)
        {

            return string.Format("/{0}/query/{1}/{2}", 
                SiteName,
                stub,
                RevisionId
            );
        }

        public long RowNumber { get; set; }
        public bool Featured { get; set; }
        public bool Skipped { get; set; }
        public DateTime LastRun { get; set; }
        public int FavoriteCount { get; set; }
        public int? CreatorId { get; set; }
        public string CreatorLogin { get; set; }
        public int Views { get; set; }
        public string SiteName { get; set; }
        public string Description { get; set; }
        public string SQL { get; set; }
        public int? RevisionId { get; set; }
        public int QuerySetId { get; set; }
        public DateTime CreationDate { get; set; }

        public User Creator { get; set; }
        public User Editor { get; set; }
    }
}