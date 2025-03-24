using AuditLoggingApp.Models;
using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;
using System.Collections.Concurrent;

namespace AuditLoggingApp.Services
{
    public class AuditLogService : IAuditLogService, IDisposable
    {
        private readonly string _connectionString;
        private readonly ILogger<AuditLogService> _logger;
        private readonly ConcurrentQueue<AuditLog> _logQueue; // Thread-safe queue
        private readonly int _batchSize; // Max logs per flush
        private readonly TimeSpan _flushInterval; // Time between flushes
        private readonly CancellationTokenSource _cts; // For stopping the background task
        private Task? _flushTask; // Background flush task

        public AuditLogService(IConfiguration config, ILogger<AuditLogService> logger)
        {
            _connectionString = config.GetConnectionString("DefaultConnection")!;
            _logger = logger;
            _logQueue = new ConcurrentQueue<AuditLog>();

            var auditConfig = config.GetSection("AuditLogging").Get<AuditLoggingConfig>() ?? new AuditLoggingConfig();
            _batchSize = auditConfig.BatchSize;
            _flushInterval = TimeSpan.FromSeconds(auditConfig.FlushIntervalSeconds);

            _cts = new CancellationTokenSource();
            _flushTask = Task.Run(() => FlushQueuePeriodically(_cts.Token));
        }

        public async Task LogAsync(AuditLog auditLog)
        {
            _logQueue.Enqueue(auditLog);

            // If queue exceeds batch size, trigger an immediate flush
            if (_logQueue.Count >= _batchSize)
            {
                await FlushQueueAsync();
            }
        }

        private async Task FlushQueuePeriodically(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_flushInterval, cancellationToken); // Wait for interval
                    if (!_logQueue.IsEmpty)
                    {
                        await FlushQueueAsync();
                    }
                }
                catch (TaskCanceledException)
                {
                    // Graceful shutdown
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in periodic flush task.");
                }
            }

            // Flush remaining logs on shutdown
            if (!_logQueue.IsEmpty)
            {
                await FlushQueueAsync();
            }
        }

        private async Task FlushQueueAsync()
        {
            if (_logQueue.IsEmpty) return;

            var logsToFlush = new List<AuditLog>();
            while (_logQueue.TryDequeue(out var log) && logsToFlush.Count < _batchSize)
            {
                logsToFlush.Add(log);
            }

            if (logsToFlush.Count == 0) return;

            try
            {
                using var connection = new NpgsqlConnection(_connectionString);
                await connection.OpenAsync();

                var sql = @"
                INSERT INTO AuditLogs (Timestamp, UserId, ClientIp, HttpMethod, Path, RequestBody, ResponseStatus, ExecutionDurationMs)
                VALUES (@Timestamp, @UserId, @ClientIp, @HttpMethod, @Path, @RequestBody, @ResponseStatus, @ExecutionDurationMs)";

                // Bulk insert with Dapper
                await connection.ExecuteAsync(sql, logsToFlush);

                _logger.LogInformation("Flushed {Count} audit logs to database.", logsToFlush.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to flush {Count} audit logs to database. Writing to fallback.", logsToFlush.Count);
                await WriteToFallbackAsync(logsToFlush);
            }
        }

        private async Task WriteToFallbackAsync(List<AuditLog> logs)
        {
            var fallbackLines = logs.Select(log =>
                $"{log.Timestamp}: {log.Path} - {log.HttpMethod} - {log.ResponseStatus}");
            await File.AppendAllLinesAsync("audit-fallback.log", fallbackLines);
        }

        public void Dispose()
        {
            _cts.Cancel();
            _flushTask?.Wait(); // Wait for the final flush to complete
            _cts.Dispose();
            _flushTask?.Dispose();
        }
    }
}
