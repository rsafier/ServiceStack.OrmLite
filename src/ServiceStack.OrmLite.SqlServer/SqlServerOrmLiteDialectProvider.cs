using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ServiceStack.OrmLite.SqlServer.Converters;
using ServiceStack.Text;
#if NETSTANDARD1_3
using ApplicationException = System.InvalidOperationException;
#endif

namespace ServiceStack.OrmLite.SqlServer
{
    public class SqlServerOrmLiteDialectProvider : OrmLiteDialectProviderBase<SqlServerOrmLiteDialectProvider>
    {
        public static SqlServerOrmLiteDialectProvider Instance = new SqlServerOrmLiteDialectProvider();

        public SqlServerOrmLiteDialectProvider()
        {
            base.AutoIncrementDefinition = "IDENTITY(1,1)";
            base.SelectIdentitySql = "SELECT SCOPE_IDENTITY()";

            base.InitColumnTypeMap();

            RowVersionConverter = new SqlServerRowVersionConverter();

            base.RegisterConverter<string>(new SqlServerStringConverter());
            base.RegisterConverter<bool>(new SqlServerBoolConverter());

            base.RegisterConverter<sbyte>(new SqlServerSByteConverter());
            base.RegisterConverter<ushort>(new SqlServerUInt16Converter());
            base.RegisterConverter<uint>(new SqlServerUInt32Converter());
            base.RegisterConverter<ulong>(new SqlServerUInt64Converter());

            base.RegisterConverter<float>(new SqlServerFloatConverter());
            base.RegisterConverter<double>(new SqlServerDoubleConverter());
            base.RegisterConverter<decimal>(new SqlServerDecimalConverter());

            base.RegisterConverter<DateTime>(new SqlServerDateTimeConverter());

            base.RegisterConverter<Guid>(new SqlServerGuidConverter());

            base.RegisterConverter<byte[]>(new SqlServerByteArrayConverter());

            this.Variables = new Dictionary<string, string>
            {
                { OrmLiteVariables.SystemUtc, "SYSUTCDATETIME()" },
            };
        }

        public override string GetQuotedValue(string paramValue)
        {
            return (StringConverter.UseUnicode ? "N'" : "'") + paramValue.Replace("'", "''") + "'";
        }

        public override IDbConnection CreateConnection(string connectionString, Dictionary<string, string> options)
        {
            var isFullConnectionString = connectionString.Contains(";");

            if (!isFullConnectionString)
            {
                var filePath = connectionString;

                var filePathWithExt = filePath.EndsWithIgnoreCase(".mdf")
                    ? filePath
                    : filePath + ".mdf";

                var fileName = Path.GetFileName(filePathWithExt);
                var dbName = fileName.Substring(0, fileName.Length - ".mdf".Length);

                connectionString = $@"Data Source=.\SQLEXPRESS;AttachDbFilename={filePathWithExt};Initial Catalog={dbName};Integrated Security=True;User Instance=True;";
            }

            if (options != null)
            {
                foreach (var option in options)
                {
                    if (option.Key.ToLower() == "read only")
                    {
                        if (option.Value.ToLower() == "true")
                        {
                            connectionString += "Mode = Read Only;";
                        }
                        continue;
                    }
                    connectionString += option.Key + "=" + option.Value + ";";
                }
            }

            return new SqlConnection(connectionString);
        }

        [Obsolete("Use SqlServerDialect.Provider.RegisterConverter<DateTime>(new SqlServerDateTime2Converter());")]
        public void UseDatetime2(bool shouldUseDatetime2)
        {
            RegisterConverter<DateTime>(shouldUseDatetime2
                ? new SqlServerDateTime2Converter()
                : new SqlServerDateTimeConverter());
        }

        [Obsolete("Use GetDateTimeConverter().DateStyle = DateTimeKind.Utc")]
        public void EnsureUtc(bool shouldEnsureUtc)
        {
            this.GetDateTimeConverter().DateStyle = DateTimeKind.Utc;
        }

        public override SqlExpression<T> SqlExpression<T>()
        {
            return new SqlServerExpression<T>(this);
        }

        public override IDbDataParameter CreateParam()
        {
            return new SqlParameter();
        }

        public override bool DoesTableExist(IDbCommand dbCmd, string tableName, string schema = null)
        {
            var sql = "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = {0}"
                .SqlFmt(tableName);

            if (schema != null)
                sql += " AND TABLE_SCHEMA = {0}".SqlFmt(schema);

            var result = dbCmd.ExecLongScalar(sql);

            return result > 0;
        }

        public override bool DoesColumnExist(IDbConnection db, string columnName, string tableName, string schema = null)
        {
            var sql = "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = @tableName AND COLUMN_NAME = @columnName"
                .SqlFmt(tableName, columnName);

            if (schema != null)
                sql += " AND TABLE_SCHEMA = @schema";

            var result = db.SqlScalar<long>(sql, new { tableName, columnName, schema });

            return result > 0;
        }

        public override string GetForeignKeyOnDeleteClause(ForeignKeyConstraint foreignKey)
        {
            return "RESTRICT" == (foreignKey.OnDelete ?? "").ToUpper()
                ? ""
                : base.GetForeignKeyOnDeleteClause(foreignKey);
        }

        public override string GetForeignKeyOnUpdateClause(ForeignKeyConstraint foreignKey)
        {
            return "RESTRICT" == (foreignKey.OnUpdate ?? "").ToUpper()
                ? ""
                : base.GetForeignKeyOnUpdateClause(foreignKey);
        }

        public override string GetDropForeignKeyConstraints(ModelDefinition modelDef)
        {
            //TODO: find out if this should go in base class?

            var sb = StringBuilderCache.Allocate();
            foreach (var fieldDef in modelDef.FieldDefinitions)
            {
                if (fieldDef.ForeignKey != null)
                {
                    var foreignKeyName = fieldDef.ForeignKey.GetForeignKeyName(
                        modelDef,
                        OrmLiteUtils.GetModelDefinition(fieldDef.ForeignKey.ReferenceType),
                        NamingStrategy,
                        fieldDef);

                    var tableName = GetQuotedTableName(modelDef);
                    sb.AppendLine($"IF EXISTS (SELECT name FROM sys.foreign_keys WHERE name = '{foreignKeyName}')");
                    sb.AppendLine("BEGIN");
                    sb.AppendLine($"  ALTER TABLE {tableName} DROP {foreignKeyName};");
                    sb.AppendLine("END");
                }
            }

            return StringBuilderCache.ReturnAndFree(sb);
        }

        public override string ToAddColumnStatement(Type modelType, FieldDefinition fieldDef)
        {
            var column = GetColumnDefinition(fieldDef.FieldName,
                                             fieldDef.ColumnType,
                                             fieldDef.IsPrimaryKey,
                                             fieldDef.AutoIncrement,
                                             fieldDef.IsNullable,
                                             fieldDef.IsRowVersion,
                                             fieldDef.FieldLength,
                                             fieldDef.Scale,
                                             fieldDef.DefaultValue,
                                             fieldDef.CustomFieldDefinition);

            var modelName = GetQuotedTableName(GetModel(modelType).ModelName);

            return $"ALTER TABLE {modelName} ADD {column};";
        }

        public override string ToAlterColumnStatement(Type modelType, FieldDefinition fieldDef)
        {
            var column = GetColumnDefinition(fieldDef.FieldName,
                                             fieldDef.ColumnType,
                                             fieldDef.IsPrimaryKey,
                                             fieldDef.AutoIncrement,
                                             fieldDef.IsNullable,
                                             fieldDef.IsRowVersion,
                                             fieldDef.FieldLength,
                                             fieldDef.Scale,
                                             fieldDef.DefaultValue,
                                             fieldDef.CustomFieldDefinition);

            var modelName = GetQuotedTableName(GetModel(modelType).ModelName);

            return $"ALTER TABLE {modelName} ALTER COLUMN {column};";
        }

        public override string ToChangeColumnNameStatement(Type modelType, FieldDefinition fieldDef, string oldColumnName)
        {
            var modelName = NamingStrategy.GetTableName(GetModel(modelType).ModelName);

            var objectName = $"{modelName}.{oldColumnName}";

            return $"EXEC sp_rename {GetQuotedValue(objectName)}, {GetQuotedValue(fieldDef.FieldName)}, {GetQuotedValue("COLUMN")};";
        }

        public override string GetColumnDefinition(string fieldName, Type fieldType, bool isPrimaryKey, bool autoIncrement,
            bool isNullable, bool isRowVersion, int? fieldLength, int? scale, string defaultValue, string customFieldDefinition)
        {
            if (isRowVersion)
                return $"{fieldName} rowversion NOT NULL";

            var definition = base.GetColumnDefinition(fieldName, fieldType, isPrimaryKey, autoIncrement,
                isNullable, isRowVersion, fieldLength, scale, defaultValue, customFieldDefinition);

            return definition;
        }

        public override string ToSelectStatement(ModelDefinition modelDef,
            string selectExpression,
            string bodyExpression,
            string orderByExpression = null,
            int? offset = null,
            int? rows = null)
        {
            var sb = StringBuilderCache.Allocate()
                .Append(selectExpression)
                .Append(bodyExpression);

            if (!offset.HasValue && !rows.HasValue)
                return StringBuilderCache.ReturnAndFree(sb) + orderByExpression;

            if (offset.HasValue && offset.Value < 0)
                throw new ArgumentException($"Skip value:'{offset.Value}' must be>=0");

            if (rows.HasValue && rows.Value < 0)
                throw new ArgumentException($"Rows value:'{rows.Value}' must be>=0");

            var skip = offset.HasValue ? offset.Value : 0;
            var take = rows.HasValue ? rows.Value : int.MaxValue;

            var selectType = selectExpression.StartsWithIgnoreCase("SELECT DISTINCT") ? "SELECT DISTINCT" : "SELECT";

            //Temporary hack till we come up with a more robust paging sln for SqlServer
            if (skip == 0)
            {
                var sql = StringBuilderCache.ReturnAndFree(sb) + orderByExpression;

                if (take == int.MaxValue)
                    return sql;

                if (sql.Length < "SELECT".Length)
                    return sql;

                return $"{selectType} TOP {take + sql.Substring(selectType.Length)}";
            }

            // Required because ordering is done by Windowing function
            if (string.IsNullOrEmpty(orderByExpression))
            {
                if (modelDef.PrimaryKey == null)
                    throw new ApplicationException("Malformed model, no PrimaryKey defined");

                orderByExpression = $"ORDER BY {this.GetQuotedColumnName(modelDef, modelDef.PrimaryKey)}";
            }

            var row = take == int.MaxValue ? take : skip + take;

            var ret = $"SELECT * FROM (SELECT {selectExpression.Substring(selectType.Length)}, ROW_NUMBER() OVER ({orderByExpression}) As RowNum {bodyExpression}) AS RowConstrainedResult WHERE RowNum > {skip} AND RowNum <= {row}";

            return ret;
        }

        //SELECT without RowNum and prefer aliases to be able to use in SELECT IN () Reference Queries
        public static string UseAliasesOrStripTablePrefixes(string selectExpression)
        {
            if (selectExpression.IndexOf('.') < 0)
                return selectExpression;

            var sb = StringBuilderCache.Allocate();
            var selectToken = selectExpression.SplitOnFirst(' ');
            var tokens = selectToken[1].Split(',');
            foreach (var token in tokens)
            {
                if (sb.Length > 0)
                    sb.Append(", ");

                var field = token.Trim();

                var aliasParts = field.SplitOnLast(' ');
                if (aliasParts.Length > 1)
                {
                    sb.Append(" " + aliasParts[aliasParts.Length - 1]);
                    continue;
                }

                var parts = field.SplitOnLast('.');
                if (parts.Length > 1)
                {
                    sb.Append(" " + parts[parts.Length - 1]);
                }
                else
                {
                    sb.Append(" " + field);
                }
            }

            var sqlSelect = selectToken[0] + " " + StringBuilderCache.ReturnAndFree(sb).Trim();
            return sqlSelect;
        }

        public override string GetLoadChildrenSubSelect<From>(SqlExpression<From> expr)
        {
            if (!expr.OrderByExpression.IsNullOrEmpty() && expr.Rows == null)
            {
                var modelDef = expr.ModelDef;
                expr.Select(this.GetQuotedColumnName(modelDef, modelDef.PrimaryKey))
                    .ClearLimits()
                    .OrderBy(""); //Invalid in Sub Selects

                var subSql = expr.ToSelectStatement();

                return subSql;
            }

            return base.GetLoadChildrenSubSelect(expr);
        }

        protected SqlConnection Unwrap(IDbConnection db)
        {
            return (SqlConnection)db.ToDbConnection();
        }

        protected SqlCommand Unwrap(IDbCommand cmd)
        {
            return (SqlCommand)cmd.ToDbCommand();
        }

        protected SqlDataReader Unwrap(IDataReader reader)
        {
            return (SqlDataReader)reader;
        }

#if ASYNC
        public override Task OpenAsync(IDbConnection db, CancellationToken token)
        {
            return Unwrap(db).OpenAsync(token);
        }

        public override Task<IDataReader> ExecuteReaderAsync(IDbCommand cmd, CancellationToken token)
        {
            return Unwrap(cmd).ExecuteReaderAsync(token).Then(x => (IDataReader)x);
        }

        public override Task<int> ExecuteNonQueryAsync(IDbCommand cmd, CancellationToken token)
        {
            return Unwrap(cmd).ExecuteNonQueryAsync(token);
        }

        public override Task<object> ExecuteScalarAsync(IDbCommand cmd, CancellationToken token)
        {
            return Unwrap(cmd).ExecuteScalarAsync(token);
        }

        public override Task<bool> ReadAsync(IDataReader reader, CancellationToken token)
        {
            return Unwrap(reader).ReadAsync(token);
        }

        public override async Task<List<T>> ReaderEach<T>(IDataReader reader, Func<T> fn, CancellationToken token)
        {
            try
            {
                var to = new List<T>();
                while (await ReadAsync(reader, token).ConfigureAwait(false))
                {
                    var row = fn();
                    to.Add(row);
                }
                return to;
            }
            finally
            {
                reader.Dispose();
            }
        }

        public override async Task<Return> ReaderEach<Return>(IDataReader reader, Action fn, Return source, CancellationToken token)
        {
            try
            {
                while (await ReadAsync(reader, token).ConfigureAwait(false))
                {
                    fn();
                }
                return source;
            }
            finally
            {
                reader.Dispose();
            }
        }

        public override async Task<T> ReaderRead<T>(IDataReader reader, Func<T> fn, CancellationToken token)
        {
            try
            {
                if (await ReadAsync(reader, token).ConfigureAwait(false))
                    return fn();

                return default(T);
            }
            finally
            {
                reader.Dispose();
            }
        }
#endif

    }
}
