﻿/* Copyright 2010 10gen Inc.
*
* Licensed under the Apache License, Version 2.0 (the "License");
* you may not use this file except in compliance with the License.
* You may obtain a copy of the License at
*
* http://www.apache.org/licenses/LICENSE-2.0
*
* Unless required by applicable law or agreed to in writing, software
* distributed under the License is distributed on an "AS IS" BASIS,
* WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
* See the License for the specific language governing permissions and
* limitations under the License.
*/

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using MongoDB.BsonLibrary;
using MongoDB.CSharpDriver.Internal;

namespace MongoDB.CSharpDriver {
    public abstract class MongoCollection {
        #region private fields
        private MongoDatabase database;
        private string name;
        private SafeMode safeMode;
        private bool assignObjectIdsOnInsert = true;
        private HashSet<string> indexCache = new HashSet<string>(); // serves as its own lock object also
        #endregion

        #region constructors
        public MongoCollection(
            MongoDatabase database,
            string name
        ) {
            ValidateCollectionName(name);
            this.database = database;
            this.name = name;
            this.safeMode = database.SafeMode;
        }
        #endregion

        #region public properties
        public MongoDatabase Database {
            get { return database; }
        }

        public string FullName {
            get { return database.Name + "." + name; }
        }

        public string Name {
            get { return name; }
        }

        public SafeMode SafeMode {
            get { return safeMode; }
            set { safeMode = value; }
        }

        public bool AssignObjectIdsOnInsert {
            get { return assignObjectIdsOnInsert; }
            set { assignObjectIdsOnInsert = value; }
        }
        #endregion

        #region public methods
        public int Count() {
            return Count<BsonDocument>(null);
        }

        public int Count<Q>(
            Q query
        ) {
            var command = new BsonDocument {
                { "count", name },
                { "query", BsonDocumentWrapper.Create(query) } // query is optional
            };
            var result = database.RunCommand(command);
            return result["n"].ToInt32();
        }

        public BsonDocument CreateIndex(
            BsonDocument keys
        ) {
            BsonDocument options = null;
            return CreateIndex(keys, options);
        }

        public BsonDocument CreateIndex(
            BsonDocument keys,
            BsonDocument options
        ) {
            var indexes = database.GetCollection("system.indexes");
            var indexName = (options != null && options.Contains("name")) ? options["name"].AsString : GetIndexName(keys);
            var index = new BsonDocument {
                { "name", indexName },
                { "ns", FullName },
                { "key", keys }
            };
            index.Merge(options);
            var result = indexes.Insert(index, SafeMode.True);
            return result;
        }

        public BsonDocument CreateIndex(
            BsonDocument keys,
            string indexName
        ) {
            return CreateIndex(keys, indexName, false);
        }

        public BsonDocument CreateIndex(
            BsonDocument keys,
            string indexName,
            bool unique
        ) {
            BsonDocument options = new BsonDocument {
                { "name", indexName },
                { unique, "unique", true }
            };
            return CreateIndex(keys, options);
        }

        public BsonDocument CreateIndex(
            params string[] keyNames
        ) {
            BsonDocument keys = new BsonDocument();
            foreach (string keyName in keyNames) {
                keys.Add(keyName, 1);
            }
            return CreateIndex(keys);
        }

        public int DataSize() {
            return Stats()["size"].ToInt32();
        }

        public IEnumerable<BsonValue> Distinct(
            string key
        ) {
            BsonDocument query = null;
            return Distinct(key, query);
        }

        public IEnumerable<BsonValue> Distinct<Q>(
            string key,
            Q query
        ) {
            var command = new BsonDocument {
                { "distinct", name },
                { "key", key },
                { "query", BsonDocumentWrapper.Create(query) } // query is optional
            };
            var result = database.RunCommand(command);
            return result["values"].AsBsonArray;
        }

        public BsonDocument DropAllIndexes() {
            return DropIndex("*");
        }

        public BsonDocument DropIndex(
            BsonDocument keys
        ) {
            string indexName = GetIndexName(keys);
            return DropIndex(indexName);
        }

        public BsonDocument DropIndex(
            params string[] keyNames
        ) {
            string indexName = GetIndexName(keyNames);
            return DropIndex(indexName);
        }

        public BsonDocument DropIndex(
            string indexName
        ) {
            lock (indexCache) {
                var command = new BsonDocument {
                    { "deleteIndexes", FullName },
                    { "index", indexName }
                };
                var result = database.RunCommand(command);
                ResetIndexCache(); // TODO: what if RunCommand throws an exception
                return result;
            }
        }

        public void EnsureIndex(
            BsonDocument keys
        ) {
            lock (indexCache) {
                string indexName = GetIndexName(keys);
                if (!indexCache.Contains(indexName)) {
                    CreateIndex(keys, indexName);
                    indexCache.Add(indexName);
                }
            }
        }

        public void EnsureIndex(
           BsonDocument keys,
           BsonDocument options
        ) {
            lock (indexCache) {
                string indexName = GetIndexName(keys);
                if (!indexCache.Contains(indexName)) {
                    CreateIndex(keys, options);
                    indexCache.Add(indexName);
                }
            }
        }

        public void EnsureIndex(
            BsonDocument keys,
            string indexName
        ) {
            lock (indexCache) {
                if (!indexCache.Contains(indexName)) {
                    CreateIndex(keys, indexName);
                    indexCache.Add(indexName);
                }
            }
        }

        public void EnsureIndex(
           BsonDocument keys,
           string indexName,
           bool unique
        ) {
            lock (indexCache) {
                if (!indexCache.Contains(indexName)) {
                    CreateIndex(keys, indexName, unique);
                    indexCache.Add(indexName);
                }
            }
        }

        public void EnsureIndex(
            params string[] keyNames
        ) {
            lock (indexCache) {
                string indexName = GetIndexName(keyNames);
                if (!indexCache.Contains(indexName)) {
                    CreateIndex(keyNames);
                    indexCache.Add(indexName);
                }
            }
        }

        public MongoCursor<Q, R> Find<Q, R>(
            Q query
        ) where R : new() {
            return new MongoCursor<Q, R>(this, query);
        }

        public MongoCursor<BsonDocument, R> FindAll<R>() where R : new() {
            BsonDocument query = null;
            return Find<BsonDocument, R>(query);
        }

        public BsonDocument FindAndModify<Q, S, U>(
            Q query,
            S sort,
            U update
        ) {
            BsonDocument fields = null;
            return FindAndModify(query, sort, update, fields, false);
        }

        public BsonDocument FindAndModify<Q, S, U>(
            Q query,
            S sort,
            U update,
            bool returnNew
        ) {
            BsonDocument fields = null;
            return FindAndModify(query, sort, update, fields, returnNew);
        }

        public BsonDocument FindAndModify<Q, S, U, F>(
            Q query,
            S sort,
            U update,
            F fields,
            bool returnNew
        ) {
            var command = new BsonDocument {
                { "findAndModify", name },
                { "query", BsonDocumentWrapper.Create(query) },
                { "sort", BsonDocumentWrapper.Create(sort) },
                { "update", BsonDocumentWrapper.Create(update) },
                { "fields", BsonDocumentWrapper.Create(fields) },
                { returnNew, "new", true }
            };
            var result = database.RunCommand(command);
            return result["value"].AsBsonDocument;
        }

        public BsonDocument FindAndRemove<Q, S>(
            Q query,
            S sort
        ) {
            var command = new BsonDocument {
                { "findAndModify", name },
                { "query", BsonDocumentWrapper.Create(query) },
                { "sort", BsonDocumentWrapper.Create(sort) },
                { "remove", true }
            };
            var result = database.RunCommand(command);
            return result["value"].AsBsonDocument;
        }

        public R FindOne<R>() where R : new() {
            return FindAll<R>().Limit(1).FirstOrDefault();
        }

        public R FindOne<Q, R>(
            Q query
        ) where R : new() {
            return Find<Q, R>(query).Limit(1).FirstOrDefault();
        }

        public IEnumerable<BsonDocument> GetIndexes() {
            var indexes = database.GetCollection("system.indexes");
            var query = new BsonDocument("ns", FullName);
            return indexes.Find(query).ToList(); // force query to execute before returning
        }

        public IEnumerable<BsonDocument> Group(
            BsonDocument keys,
            BsonDocument initial,
            BsonJavaScript reduce
        ) {
            BsonDocument query = null;
            return Group(query, keys, initial, reduce, null);
        }

        public IEnumerable<BsonDocument> Group<Q>(
            Q query,
            BsonDocument keys,
            BsonDocument initial,
            BsonJavaScript reduce
        ) {
            return Group(query, keys, initial, reduce, null);
        }

        public IEnumerable<BsonDocument> Group<Q>(
            Q query,
            BsonDocument keys,
            BsonDocument initial,
            BsonJavaScript reduce,
            BsonJavaScript finalize
        ) {
            var command = new BsonDocument {
                { "group",
                    new BsonDocument {
                        { "ns", name },
                        { "condition", BsonDocumentWrapper.Create(query) }, // condition is optional
                        { "key", keys },
                        { "initial", initial },
                        { "$reduce", reduce },
                        { "finalize", finalize }
                    }
                }
            };
            var result = database.RunCommand(command);
            return result["retval"].AsBsonArray.Values.Cast<BsonDocument>();
        }

        public IEnumerable<BsonDocument> Group<Q>(
            Q query,
            BsonJavaScript keyf,
            BsonDocument initial,
            BsonJavaScript reduce,
            BsonJavaScript finalize
        ) {
            var command = new BsonDocument {
                { "group",
                    new BsonDocument {
                        { "ns", name },
                        { "condition", BsonDocumentWrapper.Create(query) }, // condition is optional
                        { "$keyf", keyf },
                        { "initial", initial },
                        { "$reduce", reduce },
                        { "finalize", finalize }
                    } 
                }
            };
            var result = database.RunCommand(command);
            return result["retval"].AsBsonArray.Values.Cast<BsonDocument>();
        }

        public IEnumerable<BsonDocument> Group(
            string key,
            BsonDocument initial,
            BsonJavaScript reduce
        ) {
            BsonDocument query = null;
            var keys = new BsonDocument(key, 1);
            return Group(query, keys, initial, reduce, null);
        }

        // WARNING: be VERY careful about adding any new overloads of Insert or InsertBatch (just don't do it!)
        // it's very easy for the compiler to end up inferring the wrong type for I!
        // that's also why Insert and InsertBatch have to have different names

        public BsonDocument Insert<I>(
            I document
        ) {
            return Insert(document, safeMode);
        }

        public BsonDocument Insert<I>(
            I document,
            SafeMode safeMode
        ) {
            return InsertBatch<I>(new I[] { document }, safeMode);
        }

        public BsonDocument InsertBatch<I>(
            IEnumerable<I> documents
        ) {
            return InsertBatch<I>(documents, safeMode);
        }

        public BsonDocument InsertBatch<I>(
            IEnumerable<I> documents,
            SafeMode safeMode
        ) {
            if (assignObjectIdsOnInsert) {
                if (typeof(I) == typeof(BsonDocument)) {
                    AssignObjectIds((IEnumerable<BsonDocument>) documents);
                }
            }

            BsonArray batches = null;
            if (safeMode.Enabled) {
                batches = new BsonArray();
            }

            MongoConnection connection = database.GetConnection();

            using (var message = new MongoInsertMessage(FullName)) {
                message.WriteToBuffer(); // must be called before AddDocument

                foreach (var document in documents) {
                    message.AddDocument(document);
                    if (message.MessageLength > MongoDefaults.MaxMessageLength) {
                        byte[] lastDocument = message.RemoveLastDocument();
                        var intermediateError = connection.SendMessage(message, safeMode);
                        if (safeMode.Enabled) { batches.Add(intermediateError); }
                        message.ResetBatch(lastDocument);
                    }
                }

                var lastError = connection.SendMessage(message, safeMode);
                if (safeMode.Enabled) { batches.Add(lastError); }
            }

            database.ReleaseConnection(connection);

            if (safeMode.Enabled) {
                if (batches.Count() == 1) {
                    return batches[0].AsBsonDocument;
                } else {
                    return new BsonDocument("batches", batches);
                }
            } else {
                return null;
            }
        }

        public bool IsCapped() {
            throw new NotImplementedException();
        }

        public MongoMapReduceResult MapReduce<Q>(
            Q query,
            BsonJavaScript map,
            BsonJavaScript reduce
        ) {
            return MapReduce(query, map, reduce, null, null);
        }

        public MongoMapReduceResult MapReduce<Q>(
            Q query,
            BsonJavaScript map,
            BsonJavaScript reduce,
            BsonJavaScript finalize
        ) {
            return MapReduce(query, map, reduce, finalize, null);
        }

        public MongoMapReduceResult MapReduce<Q>(
            Q query,
            BsonJavaScript map,
            BsonJavaScript reduce,
            BsonJavaScript finalize,
            BsonDocument options
        ) {
            var command = new BsonDocument {
                { "mapreduce", name },
                { "query", BsonDocumentWrapper.Create(query) }, // query is optional
                { "map", map },
                { "reduce", reduce }
            };
            command.Merge(options);
            var result = database.RunCommand(command);
            return new MongoMapReduceResult(database, result);
        }

        public MongoMapReduceResult MapReduce(
            BsonJavaScript map,
            BsonJavaScript reduce
        ) {
            BsonDocument query = null;
            return MapReduce(query, map, reduce, null, null);
        }

        public MongoMapReduceResult MapReduce(
            BsonJavaScript map,
            BsonJavaScript reduce,
            BsonJavaScript finalize
        ) {
            BsonDocument query = null;
            return MapReduce(query, map, reduce, finalize, null);
        }

        public void ReIndex() {
            throw new NotImplementedException();
        }

        public BsonDocument Remove<Q>(
            Q query
        ) {
            return Remove(query, RemoveFlags.None, safeMode);
        }

        public BsonDocument Remove<Q>(
            Q query,
            SafeMode safeMode
        ) {
            return Remove(query, RemoveFlags.None, safeMode);
        }

        public BsonDocument Remove<Q>(
            Q query,
            RemoveFlags flags
        ) {
            return Remove(query, flags, safeMode);
        }

        public BsonDocument Remove<Q>(
           Q query,
           RemoveFlags flags,
           SafeMode safeMode
        ) {
            // special case for query on _id
            // TODO: find _id even when type is not BsonDocument
            if (query != null) {
                BsonDocument queryBsonDocument = query as BsonDocument;
                if (queryBsonDocument != null) {
                    if (
                        queryBsonDocument.Count == 1 &&
                        queryBsonDocument.GetElement(0).Name == "_id" &&
                        queryBsonDocument[0].BsonType == BsonType.ObjectId
                    ) {
                        flags |= RemoveFlags.Single;
                    }
                }
            }

            using (var message = new MongoDeleteMessage<Q>(FullName, flags, query)) {
                var connection = database.GetConnection();
                var lastError = connection.SendMessage(message, safeMode);
                database.ReleaseConnection(connection);
                return lastError;
            }
        }

        public BsonDocument RemoveAll() {
            BsonDocument query = null;
            return Remove(query, RemoveFlags.None, safeMode);
        }

        public BsonDocument RemoveAll(
           SafeMode safeMode
        ) {
            BsonDocument query = null;
            return Remove(query, RemoveFlags.None, safeMode);
        }

        public void ResetIndexCache() {
            lock (indexCache) {
                indexCache.Clear();
            }
        }

        public BsonDocument Save(
            BsonDocument document
        ) {
            return Save(document, safeMode);
        }

        // only works with BsonDocuments for now
        // reason: how do we find the _id value for an arbitrary class?
        public BsonDocument Save(
            BsonDocument document,
            SafeMode safeMode
        ) {
            BsonValue id = document["_id", null];
            if (id == null) {
                id = BsonObjectId.GenerateNewId();
                document["_id"] = id; // TODO: do we need to make sure it's the first element?
                return Insert(document, safeMode);
            } else {
                var query = new BsonDocument("_id", id);
                return Update(query, document, UpdateFlags.Upsert, safeMode);
            }
        }

        public BsonDocument Stats() {
            var command = new BsonDocument("collstats", name);
            return database.RunCommand(command);
        }

        public int StorageSize() {
            return Stats()["storageSize"].ToInt32();
        }

        public int TotalIndexSize() {
            return Stats()["totalIndexSize"].ToInt32();
        }

        public int TotalSize() {
            var totalSize = StorageSize();
            var indexes = GetIndexes();
            foreach (var index in indexes) {
                var indexName = index["name"].AsString;
                var indexCollectionName = string.Format("{0}.${1}", name, indexName);
                var indexCollection = database.GetCollection(indexCollectionName);
                totalSize += indexCollection.DataSize();
            }
            return totalSize;
        }

        public override string ToString() {
 	         return FullName;
        }

        public BsonDocument Update<Q, U>(
            Q query,
            U update
        ) where U : new() {
            return Update(query, update, UpdateFlags.None, safeMode);
        }

        public BsonDocument Update<Q, U>(
            Q query,
            U update,
            SafeMode safeMode
        ) where U : new() {
            return Update(query, update, UpdateFlags.None, safeMode);
        }

        public BsonDocument Update<Q, U>(
            Q query,
            U update,
            UpdateFlags flags
        ) where U : new() {
            return Update(query, update, flags, safeMode);
        }

        public BsonDocument Update<Q, U>(
            Q query,
            U update,
            UpdateFlags flags,
            SafeMode safeMode
        ) where U : new() {
            // TODO: remove this sanity check or make it configurable?
            var queryBsonDocument = query as BsonDocument;
            if (queryBsonDocument != null) {
                if (queryBsonDocument.Any(e => e.Name.StartsWith("$"))) {
                    throw new BsonException("Found atomic modifiers in query (are your arguments to Update in the wrong order?)");
                }
            }

            using (var message = new MongoUpdateMessage<Q, U>(FullName, flags, query, update)) {
                var connection = database.GetConnection();
                var lastError = connection.SendMessage(message, safeMode);
                database.ReleaseConnection(connection);
                return lastError;
            }
        }

        public BsonDocument Validate() {
            var command = new BsonDocument("validate", name);
            return database.RunCommand(command);
        }
        #endregion

        #region private methods
        private void AssignObjectIds(
            IEnumerable<BsonDocument> documents
        ) {
            foreach (var document in documents) {
                if (!document.Contains("_id")) {
                    // TODO: do we need to add in _id as the first field?
                    document.Add("_id", BsonObjectId.GenerateNewId());
                }
            }
        }

        private string GetIndexName(
            BsonDocument keys
        ) {
            StringBuilder sb = new StringBuilder();
            foreach (var element in keys) {
                if (sb.Length > 0) {
                    sb.Append("_");
                }
                sb.Append(element.Name);
                sb.Append("_");
                var value = element.Value;
                if (
                    value.BsonType == BsonType.Int32 ||
                    value.BsonType == BsonType.Int64 ||
                    value.BsonType == BsonType.Double ||
                    value.BsonType == BsonType.String
                ) {
                    sb.Append(value.RawValue.ToString().Replace(' ', '_'));
                }
            }
            return sb.ToString();
        }

        private string GetIndexName(
            string[] keyNames
        ) {
            StringBuilder sb = new StringBuilder();
            foreach (string name in keyNames) {
                if (sb.Length > 0) {
                    sb.Append("_");
                }
                sb.Append(name);
                sb.Append("_1");
            }
            return sb.ToString();
        }

        private void ValidateCollectionName(
            string name
        ) {
            if (name == null) {
                throw new ArgumentNullException("name");
            }
            if (
                name == "" ||
                name.Contains('\0') ||
                Encoding.UTF8.GetBytes(name).Length > 121
            ) {
                throw new MongoException("Invalid collection name");
            }
        }
        #endregion
    }

    // this subclass provides a default result document type for Find methods
    // you can still Find any other document types by using the Find<R> methods

    public class MongoCollection<D> : MongoCollection where D : new() {
        #region constructors
        public MongoCollection(
            MongoDatabase database,
            string name
        )
            : base(database, name) {
        }
        #endregion

        #region public methods
        public MongoCursor<Q, D> Find<Q>(
            Q query
        ) {
            return Find<Q, D>(query);
        }

        public MongoCursor<BsonDocument, D> FindAll() {
            return FindAll<D>();
        }

        public D FindOne() {
            return FindOne<D>();
        }

        public D FindOne<Q>(
            Q query
        ) {
            return FindOne<Q, D>(query);
        }
        #endregion
    }
}