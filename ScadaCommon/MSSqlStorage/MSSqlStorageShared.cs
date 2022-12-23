// Copyright (c) Rapid Software LLC. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using Microsoft.Data.SqlClient;
using Scada.Data.Tables;
using Scada.Dbms;
using Scada.Lang;
using System.ComponentModel;

namespace Scada.Storages.MSSqlStorage
{
    /// <summary>
    /// The class provides helper methods for SQL Server storage shared with other assemblies.
    /// </summary>
    internal static class MSSqlStorageShared
    {

        /// <summary>
        /// The database schema.
        /// </summary>
        public const string Schema = "project";


        /// <summary>
        /// Creates a database connection.
        /// </summary>
        public static  SqlConnection CreateDbConnection(DbConnectionOptions options)
        {
            string connectionString = options.ConnectionString;

            if (string.IsNullOrEmpty(connectionString))
            { 
                connectionString = string.Format("Server={0};Database={1};User ID={2};Password={3}",
                        options.Server, options.Database, options.Username, options.Password);
            }

            return new SqlConnection(connectionString);
        }

 

        /// <summary>
        /// Gets the name of the configuration database table.
        /// </summary>
        public static string GetBaseTableName(IBaseTable baseTable)
        {
            return $"{Schema}.\"{baseTable.Name.ToLowerInvariant()}\"";
        }

        /// <summary>
        /// Gets the column name of the configuration database table.
        /// </summary>
        public static string GetBaseColumnName(string propName, bool addQuotes = true)
        {
            return addQuotes
                ? '"' + propName.ToLowerInvariant() + '"'
                : propName.ToLowerInvariant();
        }

        /// <summary>
        /// Gets the column name of the configuration database table.
        /// </summary>
        public static string GetBaseColumnName(PropertyDescriptor prop, bool addQuotes = true)
        {
            return GetBaseColumnName(prop.Name, addQuotes);
        }

        /// <summary>
        /// Reads the configuration database table.
        /// </summary>
        public static void ReadBaseTable(IBaseTable baseTable, SqlConnection conn)
        {
            string sql = "SELECT * from " + GetBaseTableName(baseTable);
            SqlCommand cmd = new SqlCommand(sql, conn);

            using (SqlDataReader reader = cmd.ExecuteReader())
            {
                if (reader.HasRows)
                {
                    // check primary key column
                    try
                    {
                        reader.GetOrdinal(GetBaseColumnName(baseTable.PrimaryKey, false));
                    }
                    catch
                    {
                        throw new ScadaException(Locale.IsRussian ?
                            "Первичный ключ \"{0}\" не найден" :
                            "Primary key \"{0}\" not found", baseTable.PrimaryKey);
                    }

                    // find column indexes
                    PropertyDescriptorCollection props = TypeDescriptor.GetProperties(baseTable.ItemType);
                    int propCnt = props.Count;
                    int[] colIdxs = new int[propCnt];

                    for (int i = 0; i < propCnt; i++)
                    {
                        try { colIdxs[i] = reader.GetOrdinal(GetBaseColumnName(props[i], false)); }
                        catch { colIdxs[i] = -1; }
                    }

                    // read rows
                    baseTable.Modified = true;

                    while (reader.Read())
                    {
                        object item = baseTable.NewItem();

                        for (int i = 0; i < propCnt; i++)
                        {
                            int colIdx = colIdxs[i];

                            if (colIdx >= 0 && !reader.IsDBNull(colIdx))
                                props[i].SetValue(item, reader[colIdx]);
                        }

                        baseTable.AddObject(item);
                    }
                }
            }
        }
    }
}
