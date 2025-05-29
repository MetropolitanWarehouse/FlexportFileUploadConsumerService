using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Threading.Tasks;

namespace FlexportDocumentUploadConsumerService
{
    static class Program
    {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        static void Main()
        {
            try
            {
#if DEBUG
                var service = new Service1();
                service.StartDebug();

                Console.WriteLine("Running in debug mode. Press any key to exit...");
                Console.ReadKey();

                service.StopDebug(); // ⬅️ Gracefully stop
#else
                ServiceBase[] ServicesToRun;
                ServicesToRun = new ServiceBase[]
                {
                new Service1()
                };
                ServiceBase.Run(ServicesToRun);
#endif
            }
            catch (Exception ex)
            {
                Console.WriteLine("Unhandled exception: " + ex.Message);
                Console.WriteLine(ex.StackTrace);
            }

            
        }
    }
}
