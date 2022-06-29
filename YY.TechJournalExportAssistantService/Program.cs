using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace YY.TechJournalExportAssistantService
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var directoryPath = AppDomain.CurrentDomain.BaseDirectory;

            string fullpath = Path.Combine(directoryPath, "log_date.txt");

            // delete file more 1 MB
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

            try
            {
                CreateHostBuilder(args).Build().Run();
            }
            catch (Exception exception)
            {

                Console.WriteLine(exception);
            }
            
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
            .UseWindowsService()
                .ConfigureServices((hostContext, services) =>
                {
                    services.AddHostedService<Worker>();
                });
    }
}
