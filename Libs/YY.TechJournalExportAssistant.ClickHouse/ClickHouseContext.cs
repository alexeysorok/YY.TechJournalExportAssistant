using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using ClickHouse.Client.ADO;
using ClickHouse.Client.Copy;
using YY.TechJournalExportAssistant.ClickHouse.Helpers;
using YY.TechJournalExportAssistant.Core;
using YY.TechJournalExportAssistant.Core.Helpers;
using YY.TechJournalExportAssistant.Core.SharedBuffer;
using YY.TechJournalReaderAssistant;
using YY.TechJournalReaderAssistant.Helpers;
using YY.TechJournalReaderAssistant.Models;

namespace YY.TechJournalExportAssistant.ClickHouse
{
    public class ClickHouseContext : IDisposable
    {
        #region Private Members

        private ClickHouseConnection _connection;

        #endregion

        #region Constructors

        public ClickHouseContext(string connectionSettings)
        {
            ClickHouseHelpers.CreateDatabaseIfNotExist(connectionSettings);

            _connection = new ClickHouseConnection(connectionSettings);
            _connection.Open();

            var cmdDDL = _connection.CreateCommand();
            cmdDDL.CommandText = Resource.Query_CreateTable_EventDataStorage;
            cmdDDL.ExecuteNonQuery();
            cmdDDL.CommandText = Resource.Query_CreateTable_LogFilesStorage;
            cmdDDL.ExecuteNonQuery();
        }

        #endregion

        #region Public Methods

        #region RowsData

        public void SaveRowsData(Dictionary<LogBufferItemKey, LogBufferItem> sourceDataFromBuffer)
        {
            List<object[]> rowsForInsert = new List<object[]>();
            List<object[]> positionsForInsert = new List<object[]>();
            Dictionary<string, LastRowsInfoByLogFile> maxPeriodByDirectories = new Dictionary<string, LastRowsInfoByLogFile>();
            string sourceServerName = Environment.MachineName;

            var dataFromBuffer = sourceDataFromBuffer
                .OrderBy(i => i.Key.Period)
                .ThenBy(i => i.Value.LogPosition.EventNumber)
                .ToList();
            long itemNumber = 0;
            foreach (var dataItem in dataFromBuffer)
            {
                itemNumber++;
                FileInfo logFileInfo = new FileInfo(dataItem.Key.LogFile);

                positionsForInsert.Add(new object[]
                {
                    dataItem.Key.Settings.TechJournalLog.Name,
                    logFileInfo.Directory?.Name ?? string.Empty,
                    DateTime.Now.Ticks + itemNumber,
                    logFileInfo.Name,
                    logFileInfo.CreationTimeUtc,
                    logFileInfo.LastWriteTimeUtc,
                    dataItem.Value.LogPosition.EventNumber,
                    dataItem.Value.LogPosition.CurrentFileData.Replace("\\", "\\\\"),
                    dataItem.Value.LogPosition.StreamPosition ?? 0
                });

                foreach (var rowData in dataItem.Value.LogRows)
                {
                    if (!maxPeriodByDirectories.TryGetValue(logFileInfo.FullName, out LastRowsInfoByLogFile lastInfo))
                    {
                        if (logFileInfo.Directory != null)
                        {
                            GetRowsDataMaxPeriodAndId(
                                dataItem.Key.Settings.TechJournalLog,
                                logFileInfo.Directory.Name,
                                logFileInfo.Name,
                                rowData.Value.Period,
                                out var maxPeriod,
                                out var maxId
                            );
                            lastInfo = new LastRowsInfoByLogFile(maxPeriod, maxId);
                            maxPeriodByDirectories.Add(logFileInfo.FullName, lastInfo);
                        }
                    }

                    bool existByPeriod = lastInfo.MaxPeriod > ClickHouseHelpers.MinDateTimeValue &&
                                         rowData.Value.Period.Truncate(TimeSpan.FromSeconds(1)) <= lastInfo.MaxPeriod;
                    bool existById = lastInfo.MaxId > 0 &&
                                     rowData.Value.Id <= lastInfo.MaxId;
                    if (existByPeriod && existById)
                        continue;

                    var eventItem = rowData.Value;
                    rowsForInsert.Add(new object[]
                        {
                            dataItem.Key.Settings.TechJournalLog.Name,
                            logFileInfo.Directory?.Name ?? string.Empty,
                            logFileInfo.Name,
                            eventItem.Id,
                            eventItem.Period,
                            eventItem.Level,
                            eventItem.Duration,
                            eventItem.DurationSec,
                            eventItem.EventName ?? string.Empty,
                            eventItem.ServerContextName ?? string.Empty,
                            eventItem.ProcessName ?? string.Empty,
                            eventItem.SessionId ?? 0,
                            eventItem.ApplicationName ?? string.Empty,
                            eventItem.ClientId ?? 0,
                            eventItem.ComputerName ?? string.Empty,
                            // add Source Server Name
                            sourceServerName ?? string.Empty,
                            eventItem.ConnectionId ?? 0,
                            eventItem.UserName ?? string.Empty,
                            eventItem.ApplicationId ?? 0,
                            eventItem.Context ?? string.Empty,
                            eventItem.ActionType.GetDescription() ?? string.Empty,
                            eventItem.Database ?? string.Empty,
                            eventItem.DatabaseCopy ?? string.Empty,
                            eventItem.DBMS.GetPresentation() ?? string.Empty,
                            eventItem.DatabasePID ?? string.Empty,
                            eventItem.PlanSQLText ?? string.Empty,
                            eventItem.Rows ?? 0,
                            eventItem.RowsAffected ?? 0,
                            eventItem.SQLText ?? string.Empty,
                            eventItem.SQLQueryOnly ?? string.Empty,
                            eventItem.SQLQueryParametersOnly ?? string.Empty,
                            eventItem.SQLQueryHash ?? string.Empty,
                            eventItem.SDBL ?? string.Empty,
                            eventItem.Description ?? string.Empty,
                            eventItem.Message ?? string.Empty,
                            eventItem.GetCustomFieldsAsJSON() ?? string.Empty
                        });
                }
            }

            if (rowsForInsert.Count > 0)
            {
                using (ClickHouseBulkCopy bulkCopyInterface = new ClickHouseBulkCopy(_connection)
                {
                    DestinationTableName = "EventData",
                    BatchSize = 100000,
                    MaxDegreeOfParallelism = 4
                })
                {
                    var bulkResult = bulkCopyInterface.WriteToServerAsync(rowsForInsert);
                    bulkResult.Wait();
                    rowsForInsert.Clear();
                }
            }

            if (positionsForInsert.Count > 0)
            {
                using (ClickHouseBulkCopy bulkCopyInterface = new ClickHouseBulkCopy(_connection)
                {
                    DestinationTableName = "LogFiles",
                    BatchSize = 100000
                })
                {
                    var bulkResult = bulkCopyInterface.WriteToServerAsync(positionsForInsert);
                    bulkResult.Wait();
                }
            }
        }

        public void SaveRowsData(TechJournalLogBase techJournalLog, 
            List<EventData> eventData, 
            string fileName,
            Dictionary<string, DateTime> maxPeriodByFiles = null)
        {
            IDictionary<string, List<EventData>> eventDataToInsert = new Dictionary<string, List<EventData>>();
            eventDataToInsert.Add(fileName, eventData);

            SaveRowsData(techJournalLog, eventDataToInsert);
        }
        
        public void SaveRowsData(TechJournalLogBase techJournalLog, 
            IDictionary<string, List<EventData>> eventData,
            Dictionary<string, LastRowsInfoByLogFile> maxPeriodByFiles = null)
        {
            if(maxPeriodByFiles == null) maxPeriodByFiles = new Dictionary<string, LastRowsInfoByLogFile>();
            List<object[]> rowsForInsert = new List<object[]>();
            foreach (var eventInfo in eventData)
            {
                FileInfo logFileInfo = new FileInfo(eventInfo.Key);
                foreach (var eventItem in eventInfo.Value)
                {
                    if (!maxPeriodByFiles.TryGetValue(logFileInfo.Name, out LastRowsInfoByLogFile lastInfo))
                    {
                        if (logFileInfo.Directory != null)
                        {
                            GetRowsDataMaxPeriodAndId(
                                techJournalLog,
                                logFileInfo.Directory.Name,
                                logFileInfo.Name,
                                eventItem.Period,
                                out var maxPeriod,
                                out var maxId
                            );
                            lastInfo = new LastRowsInfoByLogFile(maxPeriod, maxId);
                            maxPeriodByFiles.Add(logFileInfo.Name, lastInfo);
                        }
                    }

                    bool existByPeriod = lastInfo.MaxPeriod > ClickHouseHelpers.MinDateTimeValue &&
                                         eventItem.Period.Truncate(TimeSpan.FromSeconds(1)) <= lastInfo.MaxPeriod;
                    bool existById = lastInfo.MaxId > 0 &&
                                         eventItem.Id <= lastInfo.MaxId;
                    if (existByPeriod && existById)
                        continue;

                    if (logFileInfo.Directory != null)
                        rowsForInsert.Add(new object[]
                        {
                            techJournalLog.Name,
                            logFileInfo.Directory.Name,
                            logFileInfo.Name,
                            eventItem.Id,
                            eventItem.Period,
                            eventItem.Level,
                            eventItem.Duration,
                            eventItem.DurationSec,
                            eventItem.EventName ?? string.Empty,
                            eventItem.ServerContextName ?? string.Empty,
                            eventItem.ProcessName ?? string.Empty,
                            eventItem.SessionId ?? 0,
                            eventItem.ApplicationName ?? string.Empty,
                            eventItem.ClientId ?? 0,
                            eventItem.ComputerName ?? string.Empty,
                            eventItem.ConnectionId ?? 0,
                            eventItem.UserName ?? string.Empty,
                            eventItem.ApplicationId ?? 0,
                            eventItem.Context ?? string.Empty,
                            eventItem.ActionType.GetDescription() ?? string.Empty,
                            eventItem.Database ?? string.Empty,
                            eventItem.DatabaseCopy ?? string.Empty,
                            eventItem.DBMS.GetPresentation() ?? string.Empty,
                            eventItem.DatabasePID ?? string.Empty,
                            eventItem.PlanSQLText ?? string.Empty,
                            eventItem.Rows ?? 0,
                            eventItem.RowsAffected ?? 0,
                            eventItem.SQLText ?? string.Empty,
                            eventItem.SQLQueryOnly ?? string.Empty,
                            eventItem.SQLQueryParametersOnly ?? string.Empty,
                            eventItem.SQLQueryHash ?? string.Empty,
                            eventItem.SDBL ?? string.Empty,
                            eventItem.Description ?? string.Empty,
                            eventItem.Message ?? string.Empty,
                            eventItem.GetCustomFieldsAsJSON() ?? string.Empty
                        });
                }
            }

            if (rowsForInsert.Count == 0)
                return;

            using (ClickHouseBulkCopy bulkCopyInterface = new ClickHouseBulkCopy(_connection)
            {
                DestinationTableName = "EventData",
                BatchSize = 100000,
                MaxDegreeOfParallelism = 4
            })
            {
                var bulkResult = bulkCopyInterface.WriteToServerAsync(rowsForInsert);
                bulkResult.Wait();
                rowsForInsert.Clear();
            }
        }

        
        
        #endregion

        #region LogFiles

        public TechJournalPosition GetLogFilePosition(TechJournalLogBase techJournalLog, string directoryName)
        {
            var cmdGetLastLogFileInfo = _connection.CreateCommand();
            cmdGetLastLogFileInfo.CommandText =
                @"SELECT	                
	                LastEventNumber,
	                LastCurrentFileData,
	                LastStreamPosition
                FROM LogFiles AS LF
                WHERE TechJournalLog = {techJournalLog:String}
                    AND DirectoryName = {directoryName:String}
                    AND Id IN (
                        SELECT
                            MAX(Id) LastId
                        FROM LogFiles AS LF_LAST
                        WHERE LF_LAST.TechJournalLog = {techJournalLog:String}
                            AND LF_LAST.DirectoryName = {directoryName:String}
                    )";
            cmdGetLastLogFileInfo.AddParameterToCommand("techJournalLog", DbType.AnsiString, techJournalLog.Name);
            cmdGetLastLogFileInfo.AddParameterToCommand("directoryName", DbType.AnsiString, directoryName);

            TechJournalPosition output = null;
            using (var cmdReader = cmdGetLastLogFileInfo.ExecuteReader())
            {
                if (cmdReader.Read())
                {
                    string fileData = cmdReader.GetString(1)
                        .Replace("\\\\", "\\")
                        .FixNetworkPath();
                    output = new TechJournalPosition(
                        cmdReader.GetInt64(0),
                        fileData,
                        cmdReader.GetInt64(2));
                }
            }

            return output;
        }
        
        public IDictionary<string, TechJournalPosition> GetCurrentLogPositions(
            TechJournalLogBase techJournalLog)
        {
            var cmdGetLastLogFileInfo = _connection.CreateCommand();
            cmdGetLastLogFileInfo.CommandText = Resource.Query_GetActualPositions;
            cmdGetLastLogFileInfo.AddParameterToCommand("techJournalLog", DbType.AnsiString, techJournalLog.Name);

            IDictionary<string, TechJournalPosition> output = new Dictionary<string, TechJournalPosition>();
            using (var cmdReader = cmdGetLastLogFileInfo.ExecuteReader())
            {
                while (cmdReader.Read())
                {
                    string fileName = cmdReader.GetString(7).Replace("\\\\", "\\");
                    string directoryName = cmdReader.GetString(1);
                    output.Add(directoryName, new TechJournalPosition(
                        cmdReader.GetInt64(6),
                        fileName,
                        cmdReader.GetInt64(8)
                    ));
                }
            }

            return output;
        }
        
        public void SaveLogPosition(TechJournalLogBase techJournalLog, TechJournalPosition position)
        {
            SaveLogPositions(techJournalLog, new List<TechJournalPosition>()
            {
                position
            });
        }
        
        public void SaveLogPositions(TechJournalLogBase techJournalLog, List<TechJournalPosition> positions)
        {
            if(positions.Count == 0)
                return;

            long itemNumber = 0;
            var dataForInsert = positions
                .Select(position =>
                {
                    itemNumber++;
                    var dataFileInfoForPosition = new FileInfo(position.CurrentFileData);

                    return new object[]
                    {
                        techJournalLog.Name,
                        dataFileInfoForPosition.Directory?.Name ?? string.Empty,
                        DateTime.Now.Ticks + itemNumber,
                        dataFileInfoForPosition.Name,
                        dataFileInfoForPosition.CreationTimeUtc,
                        dataFileInfoForPosition.LastWriteTimeUtc,
                        position.EventNumber,
                        position.CurrentFileData.Replace("\\", "\\\\"),
                        position.StreamPosition ?? 0
                    };
                })
                .ToList();

            using (ClickHouseBulkCopy bulkCopyInterface = new ClickHouseBulkCopy(_connection)
            {
                DestinationTableName = "LogFiles",
                BatchSize = 1000000
            })
            {
                var bulkResult = bulkCopyInterface.WriteToServerAsync(dataForInsert);
                bulkResult.Wait();
            }
        }

        #endregion

        public void Dispose()
        {
            if (_connection != null)
            {
                if (_connection.State == ConnectionState.Open)
                    _connection.Close();
                _connection.Dispose();
                _connection = null;
            }
        }

        #endregion

        #region Service

        private void GetRowsDataMaxPeriodAndId(TechJournalLogBase techJournalLog,
            string directoryName, string fileName, DateTime fromPeriod,
            out DateTime maxPeriod, out long maxId)
        {
            DateTime outputMaxPeriod = DateTime.MinValue;
            long outputMaxId = 0;

            using (var command = _connection.CreateCommand())
            {
                command.CommandText =
                    @"SELECT
                        MAX(Period) AS MaxPeriod,
                        MAX(Id) AS MaxId
                    FROM EventData AS RD
                    WHERE TechJournalLog = {techJournalLog:String}
                        AND DirectoryName = {directoryName:String}
                        AND FileName = {fileName:String}
                        AND Period >= {fromPeriod:DateTime}";
                command.AddParameterToCommand("techJournalLog", techJournalLog.Name);
                command.AddParameterToCommand("directoryName", directoryName);
                command.AddParameterToCommand("fileName", fileName);
                command.AddParameterToCommand("fromPeriod", fromPeriod);
                using (var cmdReader = command.ExecuteReader())
                {
                    if (cmdReader.Read())
                    {
                        outputMaxPeriod = cmdReader.GetDateTime(0);
                        outputMaxId = cmdReader.GetInt64(1);
                    }
                }
            }

            maxPeriod = outputMaxPeriod;
            maxId = outputMaxId;
        }

        public readonly struct LastRowsInfoByLogFile
        {
            public LastRowsInfoByLogFile(DateTime maxPeriod, long maxId)
            {
                MaxPeriod = maxPeriod;
                MaxId = maxId;
            }

            public DateTime MaxPeriod { get; }
            public long MaxId { get; }
        }

        #endregion
    }
}
