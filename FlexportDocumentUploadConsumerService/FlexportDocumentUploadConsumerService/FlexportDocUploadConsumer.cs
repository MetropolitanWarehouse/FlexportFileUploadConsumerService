using Newtonsoft.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System;
using System.Configuration;
using System.Data.SqlClient;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using System.Linq;

namespace FlexportDocumentUploadConsumerService
{
    public class FlexportDocUploadConsumer
    {
        private IConnection connection;
        private IModel channel;
        private ManualResetEvent _stopSignal = new ManualResetEvent(false);
        public class FileUploadMessage
        {
            public long FileID { get; set; }
        }

        private readonly string _connectionString = ConfigurationManager.ConnectionStrings["DefaultConnection"].ConnectionString;

        public void Start()
        {
            var factory = new ConnectionFactory
            {
                HostName = ConfigurationManager.AppSettings["RabbitMQHostName"],
                UserName = ConfigurationManager.AppSettings["RabbitMQUserName"],
                Password = ConfigurationManager.AppSettings["RabbitMQPassword"]
            };

            var connection = factory.CreateConnection();
            var channel = connection.CreateModel();

            channel.QueueDeclare("Flexport_File_Upload_queue", true, false, false, null);
            var consumer = new EventingBasicConsumer(channel);

            consumer.Received += async (model, ea) =>
            {
                var body = ea.Body.ToArray();
                var message = Encoding.UTF8.GetString(body);
                var uploadMessage = JsonConvert.DeserializeObject<FileUploadMessage>(message);

                bool success = await ProcessMessageAsync(uploadMessage.FileID);

                if (success)
                {
                    channel.BasicAck(ea.DeliveryTag, false);
                }
                else
                {
                    channel.BasicNack(ea.DeliveryTag, false, false); // Consider dead-lettering
                }
            };

            channel.BasicConsume("Flexport_File_Upload_queue", false, consumer);
            Console.WriteLine("Flexport consumer started. Listening for messages...");
            //  BLOCK here to keep service alive until Stop() is called
            _stopSignal.WaitOne();
            //while (true) Thread.Sleep(1000); // Keep service running
        }

        private async Task<bool> ProcessMessageAsync(long fileID)
        {
            string presignedUrl = null;
            string apiResponse = null;
            string uploadResponse = null;

            try
            {
                dynamic fileRecord;

                using (var conn = new SqlConnection(_connectionString))
                {
                    var sql = "SELECT * FROM [PinnacleLog].[dbo].[Metropolitan_FlexportFileUploadLog] WHERE ID = @ID";
                    fileRecord = await conn.QueryFirstOrDefaultAsync(sql, new { ID = fileID });

                    if (fileRecord == null)
                        return false;
                }

                string filePath = fileRecord.FilePath;
                string shipmentID = fileRecord.ShipmentID;
                string fileType = fileRecord.FileType;
                string fileContentType = fileRecord.FileContentType ?? "application/pdf";

                // 1. Get Presigned URL from Flexport API
                string bearerToken = ConfigurationManager.AppSettings["FlexportBearerToken"];
                string apiUrl = $"https://logistics-api.flexport.com/logistics/api/2025-03/orders/rs/{shipmentID}/attachments";

                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);

                    var requestBody = new
                    {
                        attachmentType = fileType,
                        contentType = fileContentType
                    };

                    var json = JsonConvert.SerializeObject(requestBody);
                    var content = new StringContent(json, Encoding.UTF8, "application/json");

                    var response = await client.PostAsync(apiUrl, content);
                    apiResponse = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                    {
                        await LogResult(fileID, null, apiResponse, false, null, "Failed to get presigned URL");
                        return false;
                    }

                    dynamic parsed = JsonConvert.DeserializeObject(apiResponse);
                    presignedUrl = parsed?.uploadUrl;

                    if (string.IsNullOrWhiteSpace(presignedUrl))
                    {
                        await LogResult(fileID, null, apiResponse, false, null, "uploadUrl missing in response");
                        return false;
                    }
                }

                // 2. Upload File via PUT
                using (var httpClient = new HttpClient())
                using (var fileStream = File.OpenRead(filePath))
                {
                    var putContent = new StreamContent(fileStream);
                    putContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(fileContentType);

                    var putResponse = await httpClient.PutAsync(presignedUrl, putContent);
                    uploadResponse = await putResponse.Content.ReadAsStringAsync();

                    bool uploadSuccess = putResponse.IsSuccessStatusCode;

                    await LogResult(fileID, presignedUrl, apiResponse, uploadSuccess, DateTime.UtcNow, uploadResponse);

                    return uploadSuccess;
                }
            }
            catch (Exception ex)
            {
                await LogResult(fileID, presignedUrl, apiResponse, false, null, ex.Message);
                return false;
            }
        }

        private async Task LogResult(long fileID, string presignedUrl, string presignedResponse, bool uploadSuccess, DateTime? uploadTime, string apiMessage)
        {
            using (var conn = new SqlConnection(_connectionString))
            {
                var sql = @"
                    UPDATE [PinnacleLog].[dbo].[Metropolitan_FlexportFileUploadLog]
                    SET 
                        PresignedUrl = @PresignedUrl,
                        PresignedUrlApiResponseMessage = @PresignedUrlApiResponseMessage,
                        UploadAttempted = 1,
                        UploadSuccessful = @UploadSuccessful,
                        UploadTime = @UploadTime,
                        ApiResponseMessage = @ApiResponseMessage,
                        ModifiedOn = GETUTCDATE()
                    WHERE ID = @ID";

                await conn.ExecuteAsync(sql, new
                {
                    ID = fileID,
                    PresignedUrl = presignedUrl,
                    PresignedUrlApiResponseMessage = presignedResponse,
                    UploadSuccessful = uploadSuccess ? 1 : 0,
                    UploadTime = uploadTime,
                    ApiResponseMessage = apiMessage
                });
            }
        }

        public void Stop()
        {
            _stopSignal.Set(); // Unblocks the WaitOne
            channel?.Close();
            connection?.Close();
        }
    }
}
