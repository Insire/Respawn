﻿using System;
using System.Collections.Generic;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using System.Linq;
using System.Threading.Tasks;
using Respawn.Graph;

namespace Respawn
{
    public class Respawner
    {
        private IList<TemporalTable> _temporalTables = new List<TemporalTable>();
        public RespawnerOptions Options { get; }
        public string? DeleteSql { get; private set; }
        public string? ReseedSql { get; private set; }

        private Respawner(RespawnerOptions options)
        {
            Options = options;
        }

        /// <summary>
        /// Creates a <see cref="Respawner" /> based on the supplied options and connection string or name. This overload only supports SQL Server.
        /// </summary>
        /// <param name="nameOrConnectionString">Name or connection string</param>
        /// <param name="options">Options</param>
        /// <returns>A respawner with generated SQL based on the supplied connection string</returns>
        /// <exception cref="ArgumentException">Throws if the options are any other database adapter besides SQL</exception>
        public static async Task<Respawner> CreateAsync(string nameOrConnectionString, RespawnerOptions? options = default)
        {
            options ??= new RespawnerOptions();

            if (options.DbAdapter is not SqlServerDbAdapter)
            {
                throw new ArgumentException("This overload only supports the SqlDataAdapter. To use an alternative adapter, use the overload that supplies a DbConnection.", nameof(options.DbAdapter));
            }

            await using var connection = new SqlConnection(nameOrConnectionString);

            await connection.OpenAsync().ConfigureAwait(false);

            var respawner = new Respawner(options);

            await respawner.BuildDeleteTables(connection).ConfigureAwait(false);

            return respawner;
        }

        /// <summary>
        /// Creates a <see cref="Respawner"/> based on the supplied connection and options. 
        /// </summary>
        /// <param name="connection">Connection object for your target database</param>
        /// <param name="options">Options</param>
        /// <returns>A respawner with generated SQL based on the supplied connection object.</returns>
        public static async Task<Respawner> CreateAsync(DbConnection connection, RespawnerOptions? options = default)
        {
            options ??= new RespawnerOptions();

            var respawner = new Respawner(options);

            await respawner.BuildDeleteTables(connection).ConfigureAwait(false);

            return respawner;
        }

        public virtual async Task ResetAsync(string nameOrConnectionString)
        {
            await using var connection = new SqlConnection(nameOrConnectionString);

            await connection.OpenAsync().ConfigureAwait(false);

            await ResetAsync(connection).ConfigureAwait(false);
        }

        public virtual async Task ResetAsync(DbConnection connection)
        {
            if (_temporalTables.Any())
            {
                var turnOffVersioningCommandText = Options.DbAdapter.BuildTurnOffSystemVersioningCommandText(_temporalTables);
                await ExecuteAlterSystemVersioningAsync(connection, turnOffVersioningCommandText).ConfigureAwait(false);
            }

            try
            {
                await ExecuteDeleteSqlAsync(connection).ConfigureAwait(false);
            }
            finally
            {
                if (_temporalTables.Any())
                {
                    var turnOnVersioningCommandText = Options.DbAdapter.BuildTurnOnSystemVersioningCommandText(_temporalTables);
                    await ExecuteAlterSystemVersioningAsync(connection, turnOnVersioningCommandText).ConfigureAwait(false);
                }
            }
        }

        private async Task ExecuteAlterSystemVersioningAsync(DbConnection connection, string commandText)
        {
            await using var tx = await connection.BeginTransactionAsync().ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();

            cmd.CommandTimeout = Options.CommandTimeout ?? cmd.CommandTimeout;
            cmd.CommandText = commandText;
            cmd.Transaction = tx;

            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);

            await tx.CommitAsync().ConfigureAwait(false);
        }

        private async Task ExecuteDeleteSqlAsync(DbConnection connection)
        {
            await using var tx = await connection.BeginTransactionAsync().ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();

            cmd.CommandTimeout = Options.CommandTimeout ?? cmd.CommandTimeout;
            cmd.CommandText = DeleteSql;
            cmd.Transaction = tx;

            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);

            if (ReseedSql != null)
            {
                cmd.CommandText = ReseedSql;
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            await tx.CommitAsync().ConfigureAwait(false);
        }

        private async Task BuildDeleteTables(DbConnection connection)
        {
            var allTables = await GetAllTables(connection).ConfigureAwait(false);

            if (!allTables.Any())
            {
                throw new InvalidOperationException(
                    "No tables found. Ensure your target database has at least one non-ignored table to reset. Consider initializing the database and/or running migrations.");
            }

            if (Options.CheckTemporalTables && await Options.DbAdapter.CheckSupportsTemporalTables(connection))
            {
                _temporalTables = await GetAllTemporalTables(connection).ConfigureAwait(false);
            }

            var allRelationships = await GetRelationships(connection).ConfigureAwait(false);

            var graphBuilder = new GraphBuilder(allTables, allRelationships);

            DeleteSql = Options.DbAdapter.BuildDeleteCommandText(graphBuilder);
            ReseedSql = Options.WithReseed ? Options.DbAdapter.BuildReseedSql(graphBuilder.ToDelete) : null;
        }

        private async Task<HashSet<Relationship>> GetRelationships(DbConnection connection)
        {
            var relationships = new HashSet<Relationship>();
            var commandText = Options.DbAdapter.BuildRelationshipCommandText(Options);

            await using var cmd = connection.CreateCommand();

            cmd.CommandText = commandText;

            await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);

            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                relationships.Add(new Relationship(
                    new Table(await reader.IsDBNullAsync(0).ConfigureAwait(false) ? null : reader.GetString(0), reader.GetString(1)),
                    new Table(await reader.IsDBNullAsync(2).ConfigureAwait(false) ? null : reader.GetString(2), reader.GetString(3)),
                    reader.GetString(4)));
            }

            return relationships;
        }

        private async Task<HashSet<Table>> GetAllTables(DbConnection connection)
        {
            var tables = new HashSet<Table>();

            var commandText = Options.DbAdapter.BuildTableCommandText(Options);

            await using var cmd = connection.CreateCommand();

            cmd.CommandText = commandText;

            await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);

            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                tables.Add(new Table(await reader.IsDBNullAsync(0).ConfigureAwait(false) ? null : reader.GetString(0), reader.GetString(1)));
            }

            return tables;
        }

        private async Task<IList<TemporalTable>> GetAllTemporalTables(DbConnection connection)
        {
            var tables = new List<TemporalTable>();

            var commandText = Options.DbAdapter.BuildTemporalTableCommandText(Options);

            await using var cmd = connection.CreateCommand();

            cmd.CommandText = commandText;

            await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);

            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                tables.Add(new TemporalTable(await reader.IsDBNullAsync(0).ConfigureAwait(false) ? null : reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
            }

            return tables;
        }
    }
}
