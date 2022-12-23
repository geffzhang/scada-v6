﻿// Copyright (c) Rapid Software LLC. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using Scada.Data.Tables;
using Scada.Dbms;
using Scada.Lang;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Text;
using System.Threading;
using System.Xml;
using Microsoft.Data.SqlClient;
using static Scada.Storages.MSSqlStorage.MSSqlStorageShared;
using System.Runtime.Serialization;

namespace Scada.Storages.MSSqlStorage
{
    /// <summary>
    /// Represents a storage logic.
    /// </summary>
    public class MSSqlStorageLogic : StorageLogic
    {

        /// <summary>
        /// Represents a reader that reads data from a view.
        /// </summary>
        private class ViewReader : BinaryReader
        {
            private readonly Action closeAction;

            public ViewReader(Stream stream, Action closeAction)
                : base(stream, Encoding.UTF8, false)
            {
                this.closeAction = closeAction;
            }

            protected override void Dispose(bool disposing)
            {
                try { closeAction?.Invoke(); }
                finally { base.Dispose(disposing); }
            }
        }

        /// <summary>
        /// The period of attempts to connect to the database.
        /// </summary>
        private static readonly TimeSpan ConnectAttemptPeriod = TimeSpan.FromSeconds(10);

        private TimeSpan waitTimeout;            // how long to wait for connection
        private DbConnectionOptions connOptions; // the database connection options
        private SqlConnection conn;           // the database connection


        /// <summary>
        /// Initializes a new instance of the class.
        /// </summary>
        public MSSqlStorageLogic(StorageContext storageContext)
            : base(storageContext)
        {
            waitTimeout = TimeSpan.Zero;
            connOptions = null;
            conn = null;
        }


        /// <summary>
        /// Attempts to connect to the database.
        /// </summary>
        private bool CheckConnection(out string errMsg)
        {
            try
            {
                conn.Open();
                errMsg = "";
                return true;
            }
            catch (Exception ex)
            {
                errMsg = string.Format(Locale.IsRussian ?
                    "Ошибка при соединении с БД хранилища: {0}" :
                    "Error connecting to storage database: {0}", ex.Message);
                return false;
            }
            finally
            {
                conn.Close();
            }
        }

        /// <summary>
        /// Reads contents of the varchar type.
        /// </summary>
        private string ReadVarcharContents(string tableName, ServiceApp app, string path)
        {
            string sql = $"SELECT contents FROM {tableName} WHERE app_id = @appID AND path = @path LIMIT 1";
            SqlCommand cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("appID", (int)app);
            cmd.Parameters.AddWithValue("path", NormalizePath(path));

            try
            {
                Monitor.Enter(conn);
                conn.Open();

                using (SqlDataReader reader = cmd.ExecuteReader(CommandBehavior.SingleRow))
                {
                    if (reader.Read())
                        return reader.IsDBNull(0) ? "" : reader.GetString(0);
                }
            }
            finally
            {
                conn.Close();
                Monitor.Exit(conn);
            }

            throw new FileNotFoundException(string.Format(CommonPhrases.NamedFileNotFound, path));
        }

        /// <summary>
        /// Reads contents of the bytea type.
        /// </summary>
        private byte[] ReadByteaContents(string tableName, string path)
        {
            string sql = $"SELECT contents FROM {tableName} WHERE path = @path LIMIT 1";
            SqlCommand cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("path", NormalizePath(path));

            try
            {
                Monitor.Enter(conn);
                conn.Open();

                using (SqlDataReader reader = cmd.ExecuteReader(CommandBehavior.SingleRow))
                {
                    if (reader.Read())
                        return reader.IsDBNull(0) ? Array.Empty<byte>() : (byte[])reader[0];
                }
            }
            finally
            {
                conn.Close();
                Monitor.Exit(conn);
            }

            throw new FileNotFoundException(string.Format(CommonPhrases.NamedFileNotFound, path));
        }

        /// <summary>
        /// Writes contents of the varchar type.
        /// </summary>
        private void WriteVarcharContents(string tableName, ServiceApp app, string path, string contents)
        {
            string sql = $"MERGE {tableName} WITH(serializable) AS t" +
                "USING(SELECT @appID AS app_id，@path AS path) AS appidpath" +
                "ON t.app_id = appidpath.app_id and t.path = appidpath.path" +
                "WHEN MATCHED THEN" +
                "      UPDATE  SET contents = @contents, write_time = @writeTime" +
                "WHEN NOT MATCHED THEN" +
                $"     INSERT (app_id, path, contents, write_time) VALUES (@appID, @path, @contents, @writeTime)";
           
            SqlCommand cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("appID", (int)app);
            cmd.Parameters.AddWithValue("path", NormalizePath(path));
            cmd.Parameters.AddWithValue("writeTime", DateTime.UtcNow);
            cmd.Parameters.AddWithValue("contents",
                string.IsNullOrEmpty(contents) ? (object)DBNull.Value : contents);

            try
            {
                Monitor.Enter(conn);
                conn.Open();
                cmd.ExecuteNonQuery();
            }
            finally
            {
                conn.Close();
                Monitor.Exit(conn);
            }
        }

        /// <summary>
        /// Writes contents of the bytea type.
        /// </summary>
        private void WriteByteaContents(string tableName, string path, byte[] bytes)
        {
             string sql = $"MERGE {tableName} WITH(serializable) AS t" +
    "USING(SELECT @path AS path) AS newpath" +
    "ON  t.path = newpath.path" +
    "WHEN MATCHED THEN" +
    "      UPDATE  SET contents = @contents, write_time = @writeTime" +
    "WHEN NOT MATCHED THEN" +
    $"     INSERT (path, contents, write_time) VALUES (@path, @contents, @writeTime)";

            SqlCommand cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("path", NormalizePath(path));
            cmd.Parameters.AddWithValue("writeTime", DateTime.UtcNow);
            cmd.Parameters.AddWithValue("contents",
                bytes == null || bytes.Length == 0 ? (object)DBNull.Value : bytes);

            try
            {
                Monitor.Enter(conn);
                conn.Open();
                cmd.ExecuteNonQuery();
            }
            finally
            {
                conn.Close();
                Monitor.Exit(conn);
            }
        }

        /// <summary>
        /// Opens a view for reading.
        /// </summary>
        private BinaryReader GetViewReader(string path)
        {
            string sql = $"SELECT contents FROM {GetTableName(DataCategory.View)} WHERE path = @path LIMIT 1";
            SqlCommand cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("path", NormalizePath(path));

            SqlDataReader reader = null;
            bool postponeClose = false;

            void CloseAction()
            {
                reader?.Close();
                conn.Close();
                Monitor.Exit(conn);
            }

            try
            {
                Monitor.Enter(conn);
                conn.Open();
                reader = cmd.ExecuteReader(CommandBehavior.SequentialAccess);

                if (reader.Read())
                {
                    if (reader.IsDBNull(0))
                    {
                        return new BinaryReader(new MemoryStream(0));
                    }
                    else
                    {
                        postponeClose = true;
                        return new ViewReader(reader.GetStream(0), CloseAction);
                    }
                }
            }
            finally
            {
                if (!postponeClose)
                    CloseAction();
            }

            throw new FileNotFoundException(string.Format(CommonPhrases.NamedFileNotFound, path));
        }

        /// <summary>
        /// Gets the table name according to the specified category.
        /// </summary>
        private static string GetTableName(DataCategory category)
        {
            switch (category)
            {
                case DataCategory.Config:
                    return Schema + ".app_config";

                case DataCategory.Storage:
                    return Schema + ".app_storage";

                case DataCategory.View:
                    return Schema + ".view_file";

                default:
                    throw new ScadaException("Data category not supported.");
            }
        }

        /// <summary>
        /// Sets separators in the specified path to backslashes.
        /// </summary>
        private static string NormalizePath(string path)
        {
            return string.IsNullOrEmpty(path) ? "" : path.Replace('/', '\\');
        }


        /// <summary>
        /// Loads the configuration from the XML node.
        /// </summary>
        public override void LoadConfig(XmlElement xmlElement)
        {
            base.LoadConfig(xmlElement);
            waitTimeout = TimeSpan.FromSeconds(xmlElement.GetChildAsInt("WaitTimeout"));

            if (xmlElement.SelectSingleNode("Connection") is XmlNode connectionNode)
            {
                connOptions = new DbConnectionOptions();
                connOptions.LoadFromXml(connectionNode);
            }
        }

        /// <summary>
        /// Makes the storage ready for operating.
        /// </summary>
        public override void MakeReady()
        {
            if (connOptions == null)
                throw new ScadaException(CommonPhrases.ConnOptionsNotFound);

            conn = CreateDbConnection(connOptions);

            // wait for connection
            DateTime utcNow = DateTime.UtcNow;
            DateTime startDT = utcNow;
            DateTime attempDT = DateTime.MinValue;

            while (utcNow - startDT <= waitTimeout)
            {
                if (utcNow - attempDT >= ConnectAttemptPeriod)
                {
                    attempDT = utcNow;

                    if (CheckConnection(out string errMsg))
                        break;
                    else
                        StorageContext.Log.WriteError(errMsg);
                }

                Thread.Sleep(ScadaUtils.ThreadDelay);
                utcNow = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// Closes the storage.
        /// </summary>
        public override void Close()
        {
            if (conn != null)
            {
                conn.Dispose();
                conn = null;
            }
        }

        /// <summary>
        /// Reads text from the file.
        /// </summary>
        public override string ReadText(DataCategory category, string path)
        {
            if (category == DataCategory.View)
            {
                byte[] contents = ReadByteaContents(GetTableName(category), path);
                return Encoding.UTF8.GetString(contents);
            }
            else
            {
                return ReadVarcharContents(GetTableName(category), App, path);
            }
        }

        /// <summary>
        /// Reads a byte array from the file.
        /// </summary>
        public override byte[] ReadBytes(DataCategory category, string path)
        {
            if (category == DataCategory.View)
            {
                return ReadByteaContents(GetTableName(category), path);
            }
            else
            {
                string contents = ReadVarcharContents(GetTableName(category), App, path);
                return Convert.FromBase64String(contents);
            }
        }

        /// <summary>
        /// Reads the table of the configuration database.
        /// </summary>
        public override void ReadBaseTable(IBaseTable baseTable)
        {
            try
            {
                Monitor.Enter(conn);
                conn.Open();
                MSSqlStorageShared.ReadBaseTable(baseTable, conn);
            }
            finally
            {
                conn.Close();
                Monitor.Exit(conn);
            }
        }

        /// <summary>
        /// Writes the text to the file.
        /// </summary>
        public override void WriteText(DataCategory category, string path, string contents)
        {
            if (category == DataCategory.View)
            {
                byte[] bytes = string.IsNullOrEmpty(contents) ? null : Encoding.UTF8.GetBytes(contents);
                WriteByteaContents(GetTableName(category), path, bytes);
            }
            else
            {
                WriteVarcharContents(GetTableName(category), App, path, contents);
            }
        }

        /// <summary>
        /// Writes the byte array to the file.
        /// </summary>
        public override void WriteBytes(DataCategory category, string path, byte[] bytes)
        {
            if (category == DataCategory.View)
            {
                WriteByteaContents(GetTableName(category), path, bytes);
            }
            else
            {
                string contents = bytes == null ? null : Convert.ToBase64String(bytes);
                WriteVarcharContents(GetTableName(category), App, path, contents);
            }
        }

        /// <summary>
        /// Opens a text file for reading.
        /// </summary>
        public override TextReader OpenText(DataCategory category, string path)
        {
            string contents = ReadText(category, path);
            return new StringReader(contents);
        }

        /// <summary>
        /// Opens a binary file for reading.
        /// </summary>
        public override BinaryReader OpenBinary(DataCategory category, string path)
        {
            if (category == DataCategory.View)
            {
                return GetViewReader(path);
            }
            else
            {
                byte[] bytes = ReadBytes(category, path);
                return new BinaryReader(new MemoryStream(bytes, false), Encoding.UTF8, false);
            }
        }

        /// <summary>
        /// Gets information associated with the file.
        /// </summary>
        public override ShortFileInfo GetFileInfo(DataCategory category, string path)
        {
            string sql =
                $"SELECT LEN(contents), write_time FROM {GetTableName(category)} " +
                "WHERE " + (category == DataCategory.View ? "" : "app_id = @appID AND ") +
                "path = @path LIMIT 1";

            SqlCommand cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("path", NormalizePath(path));

            if (category != DataCategory.View)
                cmd.Parameters.AddWithValue("appID", (int)App);

            try
            {
                Monitor.Enter(conn);
                conn.Open();

                using (SqlDataReader reader = cmd.ExecuteReader(CommandBehavior.SingleRow))
                {
                    if (reader.Read())
                    {
                        return new ShortFileInfo
                        {
                            Exists = true,
                            LastWriteTime = reader.IsDBNull(1)
                                ? DateTime.MinValue
                                : reader.GetDateTime(1).ToUniversalTime(),
                            Length = reader.GetInt32(0)
                        };
                    }
                }
            }
            finally
            {
                conn.Close();
                Monitor.Exit(conn);
            }

            return ShortFileInfo.FileNotExists;
        }

        /// <summary>
        /// Gets a list of file paths that match the specified pattern.
        /// </summary>
        public override ICollection<string> GetFileList(DataCategory category, string path, string searchPattern)
        {
            List<string> fileList = new List<string>();
            string sql =
                $"SELECT path FROM {GetTableName(category)} " +
                "WHERE " + (category == DataCategory.View ? "" : "app_id = @appID AND ") +
                "starts_with(path, @path) AND substr(path,@pathLen) LIKE @searchPattern";

            SqlCommand cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("path", NormalizePath(path));
            cmd.Parameters.AddWithValue("pathLen", (path ?? "").Length + 1);
            cmd.Parameters.AddWithValue("searchPattern", string.IsNullOrEmpty(searchPattern)
                ? "%"
                : searchPattern.Replace('*', '%').Replace('?', '_'));

            if (category != DataCategory.View)
                cmd.Parameters.AddWithValue("appID", (int)App);

            try
            {
                Monitor.Enter(conn);
                conn.Open();

                using (SqlDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        fileList.Add(reader.GetString(0));
                    }
                }
            }
            finally
            {
                conn.Close();
                Monitor.Exit(conn);
            }

            return fileList;
        }
    }
}
