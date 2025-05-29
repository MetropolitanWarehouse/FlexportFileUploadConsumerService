using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FlexportDocumentUploadConsumerService
{
    public partial class Service1 : ServiceBase
    {
        private FlexportDocUploadConsumer consumer;
        private Thread workerThread;
        private ManualResetEvent stopSignal = new ManualResetEvent(false);
        public Service1()
        {
            InitializeComponent();
        }

        protected override void OnStart(string[] args)
        {
            consumer = new FlexportDocUploadConsumer();

            workerThread = new Thread(() =>
            {
                try
                {
                    consumer.Start();         // This now blocks until Stop() is called
                    stopSignal.WaitOne();     // Keep the thread alive (extra safety)
                }
                catch (Exception ex)
                {
                    // Log or handle exception
                    EventLog.WriteEntry("FlexportDocUploadConsumer",
                        $"Unhandled exception in thread: {ex.Message}\n{ex.StackTrace}",
                        EventLogEntryType.Error);
                }
            });

            workerThread.IsBackground = true;
            workerThread.Start();
        }

        protected override void OnStop()
        {
            stopSignal.Set();    // Just in case
            consumer.Stop();     // Cleanly shut down consumer
            workerThread.Join(); // Wait for thread to exit
        }

        // Used for debugging without installing as a Windows Service
        public void StartDebug()
        {
            this.OnStart(null);
        }

        public void StopDebug()
        {
            this.OnStop();
        }
    }
}
