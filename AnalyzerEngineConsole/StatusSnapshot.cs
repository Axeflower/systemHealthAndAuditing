using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using HealthAndAuditShared;
using Newtonsoft.Json;

namespace AnalyzerEngineConsole
{

    public class StatusSnapShotGenerator
    {
        public DateTime LastFileGeneratedTime { get; private set; } = DateTime.MinValue;
        private ConcurrentDictionary<string, AnalyzerEngine.AnalyzerInstanceInfo> AnalyzerInfo { get; } = new ConcurrentDictionary<string, AnalyzerEngine.AnalyzerInstanceInfo>();
        private FileLogger Logger { get; }
        private bool ShallRun { get; set; }
        private DirectoryInfo LogFileFolder { get; }
        private int FileGenerationIntervalSeconds { get; }
        public StatusSnapShotGenerator(string fileSavePath, FileLogger logger, int fileGenerationIntervalSeconds = 5)
        {
            Logger = logger;
            try
            {
                if (!fileSavePath.EndsWith(@"\"))
                {
                    fileSavePath += @"\";
                }
                LogFileFolder = new DirectoryInfo(fileSavePath);
                if (!LogFileFolder.Exists)
                {
                    throw new DirectoryNotFoundException("Can not find " + fileSavePath);
                }
                FileGenerationIntervalSeconds = fileGenerationIntervalSeconds;
            }
            catch (Exception e)
            {
                Logger.AddRow($"Exception in {nameof(StatusSnapShotGenerator)} constructor. {e}");
            }
        }

        public void Reset()
        {
            AnalyzerInfo.Clear();
        }

        public void AddMessageToSnapShot(DateTime timestamp,string message)
        {
           CurrentList.Push(new TimeStampedMessage{TimeStamp = timestamp, Message = message});
        }

        public void AddAnalyzerInfoToSnapShot(AnalyzerEngine.AnalyzerInstanceInfo info)
        {
            AnalyzerInfo.AddOrUpdate(info.Name, info, (key, oldValue) =>  info);
        }

        private ConcurrentStack<TimeStampedMessage> MessageList1 { get; } = new ConcurrentStack<TimeStampedMessage>();
        private ConcurrentStack<TimeStampedMessage> MessageList2 { get; } = new ConcurrentStack<TimeStampedMessage>();

        private ConcurrentStack<TimeStampedMessage> CurrentList
        {
            get
            {
                if (List1IsCurrent)
                {
                    return MessageList1;
                }
                return MessageList2;
            }
        }

        private ConcurrentStack<TimeStampedMessage> NotCurrentList
        {
            get
            {
                if (!List1IsCurrent)
                {
                    return MessageList1;
                }
                return MessageList2;
            }
        }

        private bool List1IsCurrent { get; set; } = true;
        

        private void SwitchList()
        {
            List1IsCurrent = !List1IsCurrent;
        }

        private Queue<TimeStampedMessage> ListToExport { get; } = new Queue<TimeStampedMessage>();

        private (List<TimeStampedMessage> list,int overflow ) GetListToExport()
        {
            var listSize = 20;
            var enqueuedCounter = 0;
            while (NotCurrentList.TryPop(out TimeStampedMessage message))
            {
                if (ListToExport.Count > listSize)
                {
                    ListToExport.Dequeue();
                }
                ListToExport.Enqueue(message);
                enqueuedCounter++;
            }
            var overflow = 0;
            if (enqueuedCounter > listSize)
            {
                overflow = enqueuedCounter - listSize;
            }
            return (ListToExport.ToList(),overflow);
        }

        public void Stop()
        {
            ShallRun = false;
        }
        public void StartGenerator()
        {
            if (!ShallRun)
            {
                ShallRun = true;
                Run();
            }
        }
        private void Run()
        {
            try
            {
                var snapshotThread = new Thread(() =>
                {
                    while (ShallRun)
                    {
                        SwitchList();
                        var snapToSave = new StatusSnapshot();
                        snapToSave.GeneratedUTC = DateTime.UtcNow;
                        foreach (var info in AnalyzerInfo)
                        {
                            snapToSave.AnalyzerList.Add(info.Value);
                        }
                        var messageExport = GetListToExport();
                        foreach (var message in messageExport.list)
                        {
                            snapToSave.Messages.Add(message);
                        }
                        snapToSave.OverFlowMessages = messageExport.overflow;
                        var fileContent = JsonConvert.SerializeObject(snapToSave);
                        using (var sw = new StreamWriter(LogFileFolder.FullName + "enginestatussnapshot.json", false,Encoding.UTF8))
                        {
                            sw.Write(fileContent);
                        }
                        LastFileGeneratedTime = DateTime.UtcNow;
                        Thread.Sleep(FileGenerationIntervalSeconds * 1000);
                    }
                });
                snapshotThread.Name = nameof(snapshotThread);
                snapshotThread.Start();
            }
            catch (Exception e)
            {
                ShallRun = false;
                Logger.AddRow($"Exception in {nameof(StatusSnapShotGenerator)}.{nameof(Run)} method. {e}");
                StartGenerator();
            }
        }

        private class StatusSnapshot
        {
            public DateTime GeneratedUTC { get; set; }
            public List<TimeStampedMessage> Messages { get; } = new List<TimeStampedMessage>();
            public int OverFlowMessages { get; set; }
            public List<AnalyzerEngine.AnalyzerInstanceInfo> AnalyzerList { get; } = new List<AnalyzerEngine.AnalyzerInstanceInfo>();           
        }

        private class TimeStampedMessage
        {
            public DateTime TimeStamp { get; set; }
            public string Message { get; set; }
        }
    }

}

