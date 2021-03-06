﻿using System;
using NLog;
using Npgsql.Logging;

namespace RurouniJones.DCS.OverlordBot
{
    //NLog logging provider for npgsql
    internal class NLogLoggingProvider : INpgsqlLoggingProvider
    {
        public NpgsqlLogger CreateLogger(string name)
        {
            return new NLogLogger(name);
        }
    }

    internal class NLogLogger : NpgsqlLogger
    {
        private readonly Logger _log;

        internal NLogLogger(string name)
        {
            _log = LogManager.GetLogger(name);
        }

        public override bool IsEnabled(NpgsqlLogLevel level)
        {
            return _log.IsEnabled(ToNLogLogLevel(level));
        }

        public override void Log(NpgsqlLogLevel level, int connectorId, string msg, Exception exception = null)
        {
            var ev = new LogEventInfo(ToNLogLogLevel(level), "RurouniJones.DCS.OverlordBot.Overlord.Npgsql", msg);
            if (exception != null)
                ev.Exception = exception;
            if (connectorId != 0)
                ev.Properties["ConnectorId"] = connectorId;
            _log.Log(ev);
        }

        private static LogLevel ToNLogLogLevel(NpgsqlLogLevel level)
        {
            switch (level)
            {
                case NpgsqlLogLevel.Trace:
                    return LogLevel.Trace;
                case NpgsqlLogLevel.Debug:
                    return LogLevel.Debug;
                case NpgsqlLogLevel.Info:
                    return LogLevel.Info;
                case NpgsqlLogLevel.Warn:
                    return LogLevel.Warn;
                case NpgsqlLogLevel.Error:
                    return LogLevel.Error;
                case NpgsqlLogLevel.Fatal:
                    return LogLevel.Fatal;
                default:
                    throw new ArgumentOutOfRangeException(nameof(level));
            }
        }
    }
}
