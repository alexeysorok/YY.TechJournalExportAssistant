using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using YY.TechJournalExportAssistant.ClickHouse;
using YY.TechJournalExportAssistant.Core;
using YY.TechJournalExportAssistant.Core.SharedBuffer;
using YY.TechJournalExportAssistant.Core.SharedBuffer.EventArgs;

namespace YY.TechJournalExportAssistantService
{
    public class Worker : BackgroundService
    {
        //private readonly ILogger<Worker> _logger;

        //public Worker(ILogger<Worker> logger)
        //{
        //    _logger = logger;
        //}


        public static IConfiguration Configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings_db.json", optional: false, reloadOnChange: false)
            .Build();

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            //while (!stoppingToken.IsCancellationRequested)
            //{
            //    _logger.LogInformation("Worker running at: {time}", DateTimeOffset.Now);
            //    await Task.Delay(1000, stoppingToken);
            //}

            TechJournalSettings settings = TechJournalSettings.Create(Configuration);

            TechJournalExport exportMaster = new TechJournalExport(settings, new TechJournalOnClickHouseTargetBuilder());
            exportMaster.OnErrorEvent += OnError;
            exportMaster.OnSendLogEvent += OnSend;
            await exportMaster.StartExport();

            Console.WriteLine("Good luck & bye!");
        }

        private static void OnError(OnErrorExportSharedBufferEventArgs e)
        {
            var location = System.Reflection.Assembly.GetEntryAssembly().Location;
            var directoryPath = Path.GetDirectoryName(location);

            string fullpath = Path.Combine(directoryPath, "log_err.txt");

            Console.WriteLine($"Log name: {e?.Exception?.Settings?.Name ?? "Unknown"}\n" +
                              $"Error info: {e.Exception.ToString()}");

            var line = $"Log name: {e?.Exception?.Settings?.Name ?? "Unknown"}\n" +
                              $"Error info: {e.Exception.ToString()}";

            using StreamWriter file = new(fullpath, append: true);

            file.WriteLine(line);
        }

        private static void OnSend(OnSendLogFromSharedBufferEventArgs args)
        {

            Console.WriteLine("root path:" + AppDomain.CurrentDomain.BaseDirectory);

            Console.WriteLine($"Отправка данных в хранилище:\n" +
                              $"Записей: {args.DataFromBuffer.Values.SelectMany(i => i.LogRows).Select(i => i.Value).Count()}\n" +
                              $"Актуальных позиций чтения: {args.DataFromBuffer.Values.Select(i => i.LogPosition).Count() }");

            var line = $"Дата: {DateTime.Now}\n" + 
                              $"Отправка данных в хранилище:\n" +
                              $"Записей: {args.DataFromBuffer.Values.SelectMany(i => i.LogRows).Select(i => i.Value).Count()}\n" +
                              $"Актуальных позиций чтения: {args.DataFromBuffer.Values.Select(i => i.LogPosition).Count() }";

            //var location = System.Reflection.Assembly.GetEntryAssembly().Location;
            //var directoryPath = Path.GetDirectoryName(location);

            var directoryPath = AppDomain.CurrentDomain.BaseDirectory;

            //string path_dir = Environment.CurrentDirectory;
            string fullpath = Path.Combine(directoryPath, "log_date.txt");

            // debug file look
            //Console.WriteLine(fullpath);

            // wrote info date send
            using StreamWriter file = new(fullpath, append: true);
            file.WriteLine(line);


            long length_path = 0;

            if (File.Exists(fullpath))
            {

                // size bytes 
                length_path = new System.IO.FileInfo(fullpath).Length;
                var lenght_path_mb = length_path / 1024 / 1024;
                if (lenght_path_mb >= 1)
                {
                    Console.WriteLine("File deleted");
                    File.Delete(fullpath);
                }

            }
            
            




        }
    }
}
