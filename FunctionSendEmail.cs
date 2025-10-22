using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Text;
using System.Text.Json;

namespace EmailFunction_Getz;


public class FunctionSendEmail
{
    private readonly ILogger _logger;
    private readonly HttpClient _httpClient = new HttpClient();

    private static readonly string tenantId = Environment.GetEnvironmentVariable("TenantId");
    private static readonly string clientId = Environment.GetEnvironmentVariable("ClientId");
    private static readonly string clientSecret = Environment.GetEnvironmentVariable("ClientSecret");
    private static readonly string siteId = Environment.GetEnvironmentVariable("SiteId");
    private static readonly string myPendingUrl = Environment.GetEnvironmentVariable("MY_PENDING_URL");

    public FunctionSendEmail(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<FunctionSendEmail>();
    }

    public class EmailRequest
    {
        public string TOAddress { get; set; }
        public string CCAddress { get; set; }
        public string BCCAddress { get; set; }
        public string Subject { get; set; }
        public string Body { get; set; }
        public string BodyTemplate { get; set; } // Template text with placeholders
        public bool IsHtml { get; set; }
        public bool InclSignature { get; set; }

        // Template parameters+
        public string AppName { get; set; }
        public string DeepLink { get; set; }
        public string RecipientName { get; set; }
        public string CaseNo { get; set; }
        public string Decision { get; set; }
        public string InitiatorName { get; set; }
        public string CurrentApprover { get; set; }
        public string AdditionalNotes { get; set; }
    }

    public class UserTasks
    {
        public string UserName { get; set; }
        public string Email { get; set; }
        public List<ApplicationTask> Applications { get; set; }
    }

    public class ApplicationTask
    {
        public string ApplicationName { get; set; }
        public int TaskCount { get; set; }
        public int DelegatedTaskCount { get; set; }
    }

    [Function("DailyProcessAppsData")]
    public async Task RunAppsDataAsync([TimerTrigger("%ProcessAppsData_Timer%")] TimerInfo timer)
    {
        _logger.LogInformation($"ProcessAppsData triggered at: {DateTime.Now}");

        //var endpointUrl = "https://<your-function-app-name>.azurewebsites.net/api/process-appsData";
        var endpointUrl = "http://localhost:7065/api/process-appsData";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpointUrl);

            // ✅ Add required headers
            //request.Headers.Add("x-functions-key", "ibrin0nPBmLbP2pEf2");
            //request.Headers.Add("Accept", "application/json"); // optional

            var response = await _httpClient.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadAsStringAsync();
                _logger.LogInformation($"Successfully called ProcessApplicationData. Response: {result}");
            }
            else
            {
                _logger.LogWarning($"ProcessApplicationData returned {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error calling ProcessApplicationData: {ex.Message}");
        }

        _logger.LogInformation("ProcessAppsData completed.");
    }

    [Function("DailyProcessUserEmailData")]
    public async Task RunUserEmailDataAsync([TimerTrigger("%ProcessUserEmailData_Timer%")] TimerInfo timer)
    {
        _logger.LogInformation($"ProcessUserEmailData triggered at: {DateTime.Now}");

        //var endpointUrl = "https://<your-function-app-name>.azurewebsites.net/api/process-appsData";
        var endpointUrl = "http://localhost:7065/api/process-userEmailData";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpointUrl);

            // ✅ Add required headers
            //request.Headers.Add("x-functions-key", "ibrin0nPBmLbP2pEf2");
            //request.Headers.Add("Accept", "application/json"); // optional

            var response = await _httpClient.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadAsStringAsync();
                _logger.LogInformation($"Successfully called GetUserEmailData. Response: {result}");
            }
            else
            {
                _logger.LogWarning($"GetUserEmailData returned {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error calling GetUserEmailData: {ex.Message}");
        }

        _logger.LogInformation("ProcessUserEmailData completed.");
    }

    public async Task<bool> DeleteUserEmailData()
    {
        try
        {
            // 1️⃣ Authenticate with Graph
            var app = ConfidentialClientApplicationBuilder.Create(clientId)
                .WithClientSecret(clientSecret)
                .WithAuthority($"https://login.microsoftonline.com/{tenantId}")
                .Build();

            string[] scopes = { "https://graph.microsoft.com/.default" };
            var authResult = await app.AcquireTokenForClient(scopes).ExecuteAsync();
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", authResult.AccessToken);

            // 2️⃣ Fetch all items in UserEmailData
            string getAllItemsUrl = $"https://graph.microsoft.com/v1.0/sites/{siteId}/lists/UserEmailData/items?$select=id";
            var getResponse = await _httpClient.GetAsync(getAllItemsUrl);
            getResponse.EnsureSuccessStatusCode();
            var content = await getResponse.Content.ReadAsStringAsync();

            using var jsonDoc = JsonDocument.Parse(content);
            var items = jsonDoc.RootElement.GetProperty("value").EnumerateArray().ToList();

            // 3️⃣ Delete in batches of 20
            const int batchSize = 20;
            int batchCounter = 1;

            for (int i = 0; i < items.Count; i += batchSize)
            {
                var batchItems = items.Skip(i).Take(batchSize).ToList();

                var batchRequest = new
                {
                    requests = batchItems.Select((item, index) =>
                    {
                        var id = item.GetProperty("id").GetString();
                        return new
                        {
                            id = (index + 1).ToString(),
                            method = "DELETE",
                            url = $"/sites/{siteId}/lists/UserEmailData/items/{id}"
                        };
                    }).ToList()
                };

                var json = JsonSerializer.Serialize(batchRequest);
                var contentBatch = new StringContent(json, Encoding.UTF8, "application/json");

                var batchResponse = await _httpClient.PostAsync("https://graph.microsoft.com/v1.0/$batch", contentBatch);
                if (!batchResponse.IsSuccessStatusCode)
                {
                    var error = await batchResponse.Content.ReadAsStringAsync();
                    Console.WriteLine($"❌ Batch {batchCounter} failed: {error}");
                }
                else
                {
                    Console.WriteLine($"✅ Batch {batchCounter} deleted successfully.");
                }

                batchCounter++;
            }

            // 4️⃣ Call internal method directly
            return true;
        }
        catch (Exception ex)
        {
            return false;
        }
    }

    [Function("ProcessApplicationData")]
    public async Task<HttpResponseData> ProcessApplicationData(
    [HttpTrigger(AuthorizationLevel.Function, "get", Route = "process-appsData")] HttpRequestData req)
    {
        try
        {
            await DeleteUserEmailData();
            // 1️⃣ Authenticate with Graph
            var app = ConfidentialClientApplicationBuilder.Create(clientId)
                .WithClientSecret(clientSecret)
                .WithAuthority($"https://login.microsoftonline.com/{tenantId}")
                .Build();

            string[] scopes = { "https://graph.microsoft.com/.default" };
            var authResult = await app.AcquireTokenForClient(scopes).ExecuteAsync();
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", authResult.AccessToken);

            // 2️⃣ Get all active applications
            string applicationsUrl =
                $"https://graph.microsoft.com/v1.0/sites/{siteId}/lists/Applications/items" +
                "?$expand=fields($select=AppName,Code,AppURL,DataSource,IsActive,ListName,ApprovedBy,DelegateTo,Status)" +
                "&$select=fields" +
                "&$filter=fields/IsActive eq 1";

            var appResponse = await _httpClient.GetAsync(applicationsUrl);
            appResponse.EnsureSuccessStatusCode();
            var appContent = await appResponse.Content.ReadAsStringAsync();

            using var appDoc = JsonDocument.Parse(appContent);
            var apps = appDoc.RootElement.GetProperty("value").EnumerateArray()
                .Select(item => new
                {
                    AppName = item.GetProperty("fields").GetProperty("AppName").GetString(),
                    Code = item.GetProperty("fields").GetProperty("Code").GetString(),
                    AppURL = item.GetProperty("fields").GetProperty("AppURL").GetString(),
                    DataSource = item.GetProperty("fields").GetProperty("DataSource").GetString(),
                    IsActive = item.GetProperty("fields").GetProperty("IsActive").GetBoolean(),
                    ListName = item.GetProperty("fields").GetProperty("ListName").GetString(),
                    ApprovedBy = item.GetProperty("fields").GetProperty("ApprovedBy").GetString(),
                    DelegateTo = item.GetProperty("fields").GetProperty("DelegateTo").GetString(),
                    Status = item.GetProperty("fields").GetProperty("Status").GetString()
                })
                .ToList();

            var results = new List<object>();

            // 3️⃣ Loop each application and fetch its detail list
            foreach (var appName in apps)
            {
                // assume these come from your Application list (dynamic field names)
                string approvedByField = appName.ApprovedBy;
                string delegateToField = appName.DelegateTo;
                string statusField = appName.Status;

                // Parse the detail list response
                string detailListUrl =
                    $"https://graph.microsoft.com/v1.0/sites/{siteId}/lists/{appName.ListName}/items?" +
                    $"$expand=fields($select={approvedByField},{approvedByField}LookupId,{delegateToField},{delegateToField}LookupId)" +
                    $"&$select=fields" +
                    $"&$filter=fields/{statusField} eq 'Pending'";

                var detailResponse = await _httpClient.GetAsync(detailListUrl);
                detailResponse.EnsureSuccessStatusCode();
                var detailContent = await detailResponse.Content.ReadAsStringAsync();

                using var detailDoc = JsonDocument.Parse(detailContent);
                var items = detailDoc.RootElement.GetProperty("value");

                // Step 1️⃣: Combine Assigned and Delegated tasks into one dictionary
                var userTaskMap = new Dictionary<string, (int TaskCount, int DelegatedTaskCount)>();

                foreach (var item in items.EnumerateArray())
                {
                    var fields = item.GetProperty("fields");

                    // Assigned To / Approved By
                    if (fields.TryGetProperty(approvedByField + "LookupId", out var assignedIdProp))
                    {
                        var assignedId = assignedIdProp.GetString();
                        if (!string.IsNullOrEmpty(assignedId))
                        {
                            if (!userTaskMap.ContainsKey(assignedId))
                                userTaskMap[assignedId] = (0, 0);

                            userTaskMap[assignedId] = (
                                userTaskMap[assignedId].TaskCount + 1,
                                userTaskMap[assignedId].DelegatedTaskCount
                            );
                        }
                    }

                    // Delegated To
                    if (fields.TryGetProperty(delegateToField + "LookupId", out var delegatedIdProp))
                    {
                        var delegatedId = delegatedIdProp.GetString();
                        if (!string.IsNullOrEmpty(delegatedId))
                        {
                            if (!userTaskMap.ContainsKey(delegatedId))
                                userTaskMap[delegatedId] = (0, 0);

                            userTaskMap[delegatedId] = (
                                userTaskMap[delegatedId].TaskCount,
                                userTaskMap[delegatedId].DelegatedTaskCount + 1
                            );
                        }
                    }
                }

                // Step 2️⃣: Post combined data into SharePoint list
                foreach (var kvp in userTaskMap)
                {
                    var userId = kvp.Key;
                    var taskCount = kvp.Value.TaskCount;
                    var delegatedTaskCount = kvp.Value.DelegatedTaskCount;
                    string[] users = { userId };
                    var fields = new Dictionary<string, object>
                    {
                        ["ApplicationName"] = appName.AppName,
                        ["UserLookupId@odata.type"] = "Collection(Edm.String)",
                        ["UserLookupId"] = users,
                        ["TaskCount"] = taskCount,
                        ["DelegatedTaskCount"] = delegatedTaskCount
                    };

                    var postBody = new { fields };

                    var json = JsonSerializer.Serialize(postBody);
                    var content = new StringContent(json, Encoding.UTF8, "application/json");

                    var response = await _httpClient.PostAsync(
                        $"https://graph.microsoft.com/v1.0/sites/{siteId}/lists/UserEmailData/items",
                        content
                    );

                    if (!response.IsSuccessStatusCode)
                    {
                        var error = await response.Content.ReadAsStringAsync();
                        Console.WriteLine("Graph API error response:");
                        Console.WriteLine(error);
                    }
                    else
                    {
                        Console.WriteLine("User remainder item created successfully.");
                    }

                }

                //results.AddRange(userTaskMap);
            }

            // 6️⃣ Return grouped response
            var res = req.CreateResponse(System.Net.HttpStatusCode.OK);
            await res.WriteStringAsync(JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
            return res;

        }
        catch (Exception ex)
        {
            var res = req.CreateResponse(System.Net.HttpStatusCode.InternalServerError);
            await res.WriteStringAsync($"Error: {ex.Message}");
            return res;
        }
    }

    [Function("GetUserEmailData")]
    public async Task<HttpResponseData> GetUserEmailData(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "process-userEmailData")] HttpRequestData req)
    {
        try
        {
            // 1️⃣ Authenticate
            var app = ConfidentialClientApplicationBuilder.Create(clientId)
                .WithClientSecret(clientSecret)
                .WithAuthority($"https://login.microsoftonline.com/{tenantId}")
                .Build();

            string[] scopes = { "https://graph.microsoft.com/.default" };
            var authResult = await app.AcquireTokenForClient(scopes).ExecuteAsync();

            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", authResult.AccessToken);

            // 2️⃣ Fetch list items with only needed fields
            string graphUrl =
                $"https://graph.microsoft.com/v1.0/sites/{siteId}/lists/UserEmailData/items?$expand=fields($select=User,ApplicationName,TaskCount,DelegatedTaskCount)&$select=fields";


            var response = await _httpClient.GetAsync(graphUrl);
            response.EnsureSuccessStatusCode();

            string content = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(content);
            var items = doc.RootElement.GetProperty("value");

            // 3️⃣ Group by User (array with LookupId, LookupValue, Email)
            var grouped = items
                .EnumerateArray()
                .Where(i => i.GetProperty("fields").TryGetProperty("User", out var userProp) && userProp.ValueKind == JsonValueKind.Array && userProp.GetArrayLength() > 0)
                .GroupBy(i =>
                {
                    var user = i.GetProperty("fields").GetProperty("User")[0]; // first user in array
                    return new
                    {
                        UserName = user.GetProperty("LookupValue").GetString(),
                        Email = user.GetProperty("Email").GetString()
                    };
                })
                .Select(userGroup => new UserTasks
                {
                    UserName = userGroup.Key.UserName,
                    Email = userGroup.Key.Email,
                    Applications = userGroup
                        .Where(x => x.GetProperty("fields").TryGetProperty("ApplicationName", out _))
                        .GroupBy(x => x.GetProperty("fields").GetProperty("ApplicationName").GetString())
                        .Select(appGroup => new ApplicationTask
                        {
                            ApplicationName = appGroup.Key,
                            TaskCount = appGroup
                                .Where(x => x.GetProperty("fields").TryGetProperty("TaskCount", out _))
                                .Sum(x =>
                                {
                                    var taskProp = x.GetProperty("fields").GetProperty("TaskCount");
                                    return taskProp.ValueKind == JsonValueKind.Number ? (int)taskProp.GetDouble() : 0;
                                }),
                            DelegatedTaskCount = appGroup
                                .Where(x => x.GetProperty("fields").TryGetProperty("DelegatedTaskCount", out _))
                                .Sum(x =>
                                {
                                    var delegatedProp = x.GetProperty("fields").GetProperty("DelegatedTaskCount");
                                    return delegatedProp.ValueKind == JsonValueKind.Number ? (int)delegatedProp.GetDouble() : 0;
                                })
                        }).OrderByDescending(x => x.TaskCount)
                        .ToList()
                })
                .ToList();

            // 4️⃣ Send Emails
            await SendEmailsForAllUsers(grouped);

            // 5️⃣ Return response
            var res = req.CreateResponse(System.Net.HttpStatusCode.OK);
            await res.WriteStringAsync(JsonSerializer.Serialize(grouped, new JsonSerializerOptions { WriteIndented = true }));
            return res;
        }
        catch (Exception ex)
        {
            var res = req.CreateResponse(System.Net.HttpStatusCode.InternalServerError);
            await res.WriteStringAsync($"Error: {ex.Message}");
            return res;
        }
    }

    public async Task SendEmailsForAllUsers(List<UserTasks> users)
    {
        var httpClient = new HttpClient();
        string sendEmailFunctionUrl = Environment.GetEnvironmentVariable("BaseUrl") ?? "http://localhost:7065/api/SendEmail";

        foreach (var user in users)
        {
            var htmlBody = GenerateHtmlTable(user.UserName, user.Applications);

            var emailRequest = new EmailRequest
            {
                TOAddress = user.Email,
                Subject = $"Pending Tasks Summary - {DateTime.UtcNow:yyyy-MM-dd}",
                BodyTemplate = htmlBody
            };

            var jsonContent = new StringContent(JsonSerializer.Serialize(emailRequest), Encoding.UTF8, "application/json");
            var response = await httpClient.PostAsync(sendEmailFunctionUrl, jsonContent);

            if (response.IsSuccessStatusCode)
            {
                Console.WriteLine($"Remainder email sent to {user.Email}");
            }
            else
            {
                Console.WriteLine($"Failed to send remainder email to {user.Email}: {await response.Content.ReadAsStringAsync()}");
            }
        }
    }

    [Function("SendEmail")]
    public async Task<HttpResponseData> SendEmail(
    [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req)
    {
        _logger.LogInformation("Sending Email Triggered");

        var response = req.CreateResponse();

        try
        {
            // Parse JSON request body
            var requestBody = await JsonSerializer.DeserializeAsync<EmailRequest>(req.Body);

            if (requestBody == null || string.IsNullOrEmpty(requestBody.TOAddress))
            {
                response.StatusCode = HttpStatusCode.BadRequest;
                await response.WriteStringAsync("'To' and 'Body' are required parameters");
                return response;
            }

            // Process the template with provided parameters
            //string processedBody = ProcessTemplate(requestBody);

            // Generate email subject from template
            string emailSubject = GenerateEmailSubject(requestBody);

            // Generate email body from template
            string emailBody = GenerateEmailBody(requestBody);

            // Get SMTP configuration
            var smtpServer = Environment.GetEnvironmentVariable("SMTP_SERVER") ?? "smtp.office365.com";
            var smtpPort = int.Parse(Environment.GetEnvironmentVariable("SMTP_PORT") ?? "587");
            var smtpUser = Environment.GetEnvironmentVariable("SMTP_USER");
            var smtpPass = Environment.GetEnvironmentVariable("SMTP_PASS");
            var fromEmail = Environment.GetEnvironmentVariable("FROM_EMAIL") ?? smtpUser;
            var ENABLE_SSL = bool.TryParse(Environment.GetEnvironmentVariable("ENABLE_SSL"), out var result) ? result : false;

            if (string.IsNullOrWhiteSpace(fromEmail))
                throw new Exception("FROM_EMAIL or SMTP_USER must be configured in environment variables.");

            // Create and send email
            using (var smtpClient = new SmtpClient(smtpServer, smtpPort))
            {
                smtpClient.EnableSsl = ENABLE_SSL;
                smtpClient.Credentials = new NetworkCredential(smtpUser, smtpPass);

                var mailMessage = new MailMessage
                {
                    From = new MailAddress(fromEmail),
                    Subject = emailSubject,
                    Body = emailBody,
                    IsBodyHtml = true
                };
                mailMessage.To.Add(requestBody.TOAddress);

                if (!string.IsNullOrEmpty(requestBody.CCAddress))
                    mailMessage.CC.Add(requestBody.CCAddress);

                if (!string.IsNullOrEmpty(requestBody.BCCAddress))
                    mailMessage.Bcc.Add(requestBody.BCCAddress);

                await smtpClient.SendMailAsync(mailMessage);

                response.StatusCode = HttpStatusCode.OK;
                await response.WriteStringAsync($"Email sent successfully to {requestBody.TOAddress}");
                _logger.LogInformation($"Email sent to {requestBody.TOAddress}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending email");
            response.StatusCode = HttpStatusCode.InternalServerError;
            await response.WriteStringAsync($"Error sending email: {ex.Message}");
        }

        return response;
    }

    private string GenerateEmailSubject(EmailRequest request)
    {
        // If Subject is provided, use it as the Subject
        if (!string.IsNullOrEmpty(request.Subject))
        {
            return $@"{request.Subject} ";
        }

        string applicationName = $"{request.AppName} - "; // Replace with your actual app name

        return request.Decision?.ToLower() switch
        {
            "rejected" => $"{applicationName} Your Request is rejected by {request.CurrentApprover} - {request.CaseNo}",
            "reverted" => $"{applicationName} Your Request is reverted by {request.CurrentApprover} - {request.CaseNo}",
            "completed" => $"{applicationName} Your Request is completed - {request.CaseNo}",
            _ => $"{applicationName} Your Action is Required on a request submitted by {request.InitiatorName} - {request.CaseNo}"
        };
    }

    private string GenerateEmailBody(EmailRequest request)
    {
        if (false)
        {
            return $@"
                <!DOCTYPE html>
                <html>
                <head>
                    <style>
                        body {{ font-family: Arial, sans-serif; line-height: 1.6; color: #333; }}
                        .header {{ color: #2a5885; font-size: 18px; }}
                        .content {{ margin: 15px 0; }}
                        .button {{ 
                            display: inline-block; 
                            padding: 10px 20px; 
                            background-color: #0078d4; 
                            color: white; 
                            text-decoration: none; 
                            border-radius: 4px;
                            margin: 10px 0;
                        }}
                        .footer {{ margin-top: 20px; font-size: 12px; color: #666; }}
                    </style>
                </head>
                <body>
                    <div class='header'>Case Status Update</div>
    
                    <div class='content'>
                        <p>Dear {request.RecipientName ?? "Valued Customer"},</p>
        
                        <p>We would like to inform you about the status of your case:</p>
        
                        <table>
                            <tr><td><strong>Case Number:</strong></td><td>{request.CaseNo ?? "Not provided"}</td></tr>
                            <tr><td><strong>Decision:</strong></td><td>{request.Decision ?? "Pending"}</td></tr>
                        </table>
        
                        {(!string.IsNullOrEmpty(request.AdditionalNotes) ? $"<p><strong>Notes:</strong> {request.AdditionalNotes}</p>" : "")}
        
                        {(!string.IsNullOrEmpty(request.DeepLink) ?
                            $"<p>You can view more details by clicking the link below:</p> < a href = '{request.DeepLink}' class='button'>View Case Details</a>"
                            : "")}
                    </div>
    
                    {(request.InclSignature ? GetHtmlSignature() : "")}
    
                    <div class='footer'>
                        <p>Please do not reply to this automated message.</p>
                    </div>
                </body>
                </html>";
        }
        else
        {
            // If BodyTemplate is provided, use it as the body content
            if (!string.IsNullOrEmpty(request.BodyTemplate))
            {
                return $@"
                    <html>
                    <head>
                        <style>
                            body {{ font-family: Arial, sans-serif; line-height: 1.6; }}
                            a {{ color: #0066cc; text-decoration: none; }}
                        </style>
                    </head>
                    <body>
                        {request.BodyTemplate}
                    </body>
                    </html>";
            }

            //string salutation = $"Dear {(request.IsRejectedOrReverted ? request.InitiatorName : request.ApproverName)},";
            string salutation = string.IsNullOrEmpty(request.RecipientName) ? "Dear Concern," : $"Dear {request.RecipientName},";

            string mainContent = request.Decision?.ToLower() switch
            {
                "rejected" => $"Your request has been rejected by {request.CurrentApprover}.",
                "reverted" => $"Your request has been reverted by {request.CurrentApprover}.",
                "completed" => $"Your request has been completed.",
                _ => $"A new approval request has been submitted by {request.InitiatorName}."
            };

            string detailsLink = !string.IsNullOrEmpty(request.DeepLink)
                ? $"<a href='{request.DeepLink}'>details</a>"
                : "details";

            string actionText = request.Decision?.ToLower() switch
            {
                "rejected" => $"Please review the request {detailsLink}.",
                "reverted" => $"Please review the request {detailsLink} and take the necessary action.",
                "completed" => $"Please review the request {detailsLink}.",
                _ => $"Please review the request {detailsLink} and take the necessary action."
            };

            return $@"
                <html>
                <head>
                    <style>
                        body {{ font-family: Arial, sans-serif; line-height: 1.6; }}
                        a {{ color: #0066cc; text-decoration: none; }}
                    </style>
                </head>
                <body>
                    <p>{salutation}</p>
    
                    <p>{mainContent}</p>
    
                    <p>{actionText}</p>

                    {(!string.IsNullOrEmpty(request.AdditionalNotes) ? $"<p><strong>Notes:</strong> {request.AdditionalNotes}</p>" : "")}
    
                    {(request.InclSignature ? GetHtmlSignature() : "")}

                    <p><em>Note: This is a system-generated email. Please do not reply to this message.</em></p>
                </body>
                </html>";
        }
    }

    private string GetHtmlSignature()
    {
        return @"
            <div>
                <p>Best regards,</br> <strong>Customer Support Team</strong></p>
            </div>";
    }

    private string GetTextSignature()
    {
        return string.Join(Environment.NewLine, new[]
        {
            "Best regards,",
            "Getz Support Team"
        });
    }

    private string GetEmailSignature()
    {
        // You can make this more sophisticated or load from configuration
        return @"
            Best regards,
            Customer Support Team
            Email: support@yourcompany.com
            Phone: (123) 456-7890";
    }

    public string GenerateHtmlTable(string userName, List<ApplicationTask> applications)
    {
        var sb = new StringBuilder();

        // ✉️ Introductory Text
        sb.Append("<div style='font-family:Segoe UI, sans-serif; color:#333; line-height:1.5;'>");
        sb.Append($"<p>Dear <b>{userName}</b>,</p>");
        sb.Append($"<p>Below is a summary of your current pending and delegated tasks. <a href='{myPendingUrl}' style='color:#0078d7; text-decoration:none; font-weight:bold;'>Click here</a> to review and take action:</p>");

        // 🧩 Table
        sb.Append("<h2 style='text-align:center; color:#2f4f6f;'>My Pending Tasks</h2>");
        sb.Append("<table border='1' rules='cols' style='width:70%; margin:20px auto; border:1px solid #ddd; border-radius:10px; border-collapse:separate; overflow:hidden; font-family:Segoe UI, sans-serif;'>");
        sb.Append("<tr style='background:#f7f9fb;'>");
        sb.Append("<th style='text-align:center; padding:12px;'>Application</th>");
        sb.Append("<th style='text-align:center; padding:12px;'>Pending Tasks</th>");
        sb.Append("<th style='text-align:center; padding:12px;'>Delegated Tasks</th>");
        sb.Append("</tr>");

        foreach (var app in applications)
        {
            sb.Append("<tr>");
            sb.Append($"<td style='text-align:center; padding:12px;'>{app.ApplicationName}</td>");
            sb.Append($"<td style='text-align:center; padding:12px;'>{app.TaskCount}</td>");
            sb.Append($"<td style='text-align:center; padding:12px;'>{app.DelegatedTaskCount}</td>");
            sb.Append("</tr>");
        }

        sb.Append("</table>");

        sb.Append("<p style='margin-top:20px;'>Thank you,<br/>This is system generated email.</p>");
        sb.Append("</div>");

        return sb.ToString();

    }

}
