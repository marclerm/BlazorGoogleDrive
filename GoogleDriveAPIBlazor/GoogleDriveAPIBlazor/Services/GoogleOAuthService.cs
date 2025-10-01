using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Drive.v3;
using Google.Apis.Upload;
using System.Text.Json;

namespace GoogleDriveAPIBlazor.Services
{
    public class GoogleOAuthService
    {
        private readonly IConfiguration _config;
        private readonly IHttpClientFactory _httpClientFactory;
        // Keep a single source of truth for scopes
        private static readonly string[] DriveScopes = new[]
        {
            //DriveService.Scope.DriveReadonly,          // read content
            DriveService.Scope.Drive
        };

        public GoogleOAuthService(IConfiguration config, IHttpClientFactory httpClientFactory)
        {
            _config = config;
            _httpClientFactory = httpClientFactory;
        }

        // Get the API key from configuration
        public string GetApiKey()
        {
            return _config["Authentication:Google:ApiKey"] ?? throw new InvalidOperationException("Google API Key not found.");
        }

        /// <summary>
        /// Generates the Google OAuth 2.0 authorization URL for user login.
        /// </summary>
        /// <returns>Login URL</returns>
        public string GetGoogleLoginUrl()
        {
            var clientId = _config["Authentication:Google:ClientId"];
            var redirectUri = _config["Authentication:Google:RedirectUri"];

            // Use the same scopes you will use to build the Drive client
            // This ensures the user consents to the same permissions
            var scopes = string.Join(" ", DriveScopes);

            // Build the OAuth 2.0 authorization URL
            return
                "https://accounts.google.com/o/oauth2/v2/auth" +
                $"?response_type=code" +
                $"&client_id={clientId}" +
                $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                $"&scope={Uri.EscapeDataString(scopes)}" +
                $"&access_type=offline&prompt=consent";
        }

        /// <summary>
        /// Exchanges the authorization code for an access token.
        /// </summary>
        /// <param name="code">Code from URL</param>
        /// <returns>Response with token information</returns>
        /// <exception cref="InvalidOperationException">Exception when token response is an error</exception>
        public async Task<TokenResponse> ExchangeCodeForTokenAsync(string code)
        {           
            var client = _httpClientFactory.CreateClient();            
            var payload = new Dictionary<string, string>
            {
                ["code"] = code,
                ["client_id"] = _config["Authentication:Google:ClientId"],
                ["client_secret"] = _config["Authentication:Google:ClientSecret"],
                ["redirect_uri"] = _config["Authentication:Google:RedirectUri"],
                ["grant_type"] = "authorization_code"
            };

            var resp = await client.PostAsync("https://oauth2.googleapis.com/token",
                new FormUrlEncodedContent(payload));

            var json = await resp.Content.ReadAsStringAsync();

            // Parse both success and error shapes
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Check if the response is an error
            if (!resp.IsSuccessStatusCode)
            {
                var error = root.TryGetProperty("error", out var e) ? e.GetString() : "(no error)";
                var desc = root.TryGetProperty("error_description", out var d) ? d.GetString() : "";
                throw new InvalidOperationException($"OAuth token exchange failed: {error}. {desc}");
            }

            // Deserialize the token response           
            var token = Newtonsoft.Json.JsonConvert.DeserializeObject<TokenResponse>(json);

            // Validate the token response
            // This is a critical step to ensure the token is valid and contains the expected fields.
            if (token is null || string.IsNullOrWhiteSpace(token.AccessToken))
                throw new InvalidOperationException($"OAuth exchange did not return access_token. Raw: {json}");

            return token;
        }

        /// <summary>
        /// Retrieves files from Google Drive that match specific criteria.
        /// </summary>
        /// <param name="token">Token information</param>
        /// <param name="userKey">Just a placeholder for user key, it can be any name</param>
        /// <returns></returns>
        public async Task<List<GoogleFileModel>> GetDriveFilesAsync(TokenResponse token, string userKey = "current-user")
        {
            var drive = await CreateDriveServiceAsync(token, userKey);

            // List files (include parents so we can reconstruct paths)
            var request = drive.Files.List();
            request.Fields = "files(id,name,parents,mimeType,webViewLink,thumbnailLink,modifiedTime),nextPageToken";
            request.PageSize = 100;
            request.Q = "mimeType != 'application/vnd.google-apps.folder' " +
                        "and (name contains '.xml' or name contains '.txt' or name contains '.json')";
           
            var result = await request.ExecuteAsync();

            // Strict extension filter in C#
            var candidateFiles = result.Files?
                .Where(f => f.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                         || f.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                         || f.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                .ToList() ?? new List<Google.Apis.Drive.v3.Data.File>();

            // Build full paths using a small cache of folder lookups
            var folderCache = new Dictionary<string, (string Name, IList<string> Parents)>();

            var resultFiles = new List<GoogleFileModel>(candidateFiles.Count);
            foreach (var f in candidateFiles)
            {
                var fullPath = await ResolveFullPathAsync(drive, f.Parents, folderCache);
                // Append the file name itself
                if (!string.IsNullOrEmpty(fullPath))
                    fullPath = $"{fullPath}/{f.Name}";
                else
                    fullPath = f.Name;

                resultFiles.Add(new GoogleFileModel
                {
                    Id = f.Id,
                    Name = f.Name,
                    MimeType = f.MimeType,
                    ViewLink = f.WebViewLink,
                    Thumbnail = f.ThumbnailLink,
                    ModifiedTime = f.ModifiedTimeDateTimeOffset,
                    FullPath = fullPath
                });
            }

            return resultFiles;
        }

        /// <summary>
        /// Resolves the full path of a file in Google Drive by climbing up its parent folders.
        /// </summary>
        /// <param name="drive">An initialized <see cref="DriveService"/> instance used to query Google Drive API for folder metadata.</param>
        /// <param name="parents">A list of parent folder IDs for the file</param>
        /// <param name="cache">Cache is use to reduce redundant API calls when multiple files share the same parent folders.</param>
        /// <returns></returns>
        private async Task<string> ResolveFullPathAsync(DriveService drive, IList<string> parents, Dictionary<string, (string Name, IList<string> Parents)> cache)
        {
            // If the file is at My Drive root (no parents), return empty (caller will add file name)
            if (parents == null || parents.Count == 0)
                return string.Empty;

            // Drive allows multiple parents historically, but in most cases you'll have one.
            // We'll follow the first parent to build a single path.
            var currentId = parents[0];

            var segments = new List<string>();

            while (!string.IsNullOrEmpty(currentId))
            {
                if (currentId == "root")
                    break;

                (string Name, IList<string> Parents) node;

                // cache lookup
                if (cache.TryGetValue(currentId, out node) == false)
                {
                    var get = drive.Files.Get(currentId);
                    get.Fields = "id,name,parents";
                    get.SupportsAllDrives = true;   // important if you have shared drive items

                    var folder = await get.ExecuteAsync();
                    node = (folder.Name, folder.Parents);
                    cache[currentId] = node;
                }

                // prepend this folder name
                segments.Insert(0, node.Name);

                // climb to the next parent (if any)
                currentId = (node.Parents != null && node.Parents.Count > 0) ? node.Parents[0] : null;
            }

            // Join folder names to a path string
            return string.Join("/", segments);
        }


        /// <summary>
        /// Creates a DriveService instance using the provided token and user key.
        /// </summary>
        /// <param name="token">Token information</param>
        /// <param name="userKey">Just a placeholder for user key, it can be any name</param>
        /// <returns>DriverService to access to files</returns>
        /// <exception cref="InvalidOperationException">Exception when AccessToken value is missing from token</exception>
        private Task<DriveService> CreateDriveServiceAsync(TokenResponse token, string userKey)
        {
            if (token is null || string.IsNullOrWhiteSpace(token.AccessToken))
                throw new InvalidOperationException("Missing access token.");

            // Create the authorization flow using the provided token
            // This flow will use the token to authenticate requests
            // *The userKey is used to identify the user in the token store
            // *It can be any unique identifier, such as a user ID or email
            // *In this case, we use "current-user" as a placeholder
            var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
            {
                ClientSecrets = new ClientSecrets
                {
                    ClientId = _config["Authentication:Google:ClientId"],
                    ClientSecret = _config["Authentication:Google:ClientSecret"]
                },
                Scopes = DriveScopes
            });

            // UserCredential ties the token to a user identity + declared scopes
            var credential = new UserCredential(flow, userKey, token);

            var service = new DriveService(new Google.Apis.Services.BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = "Google Drive API - Blazor App" // Replace with your app name
            });

            return Task.FromResult(service);
        }

        /// <summary>
        /// Downloads a file from Google Drive using the provided token and file ID.
        /// </summary>
        /// <param name="token">Token</param>
        /// <param name="fileId">File ID</param>
        /// <param name="userKey">User key</param>
        /// <returns></returns>
        public async Task<(byte[] Content, string FileName, string MimeType)> DownloadFileAsync(TokenResponse token, string fileId, string userKey = "current-user")
        {
            var drive = await CreateDriveServiceAsync(token, userKey);

            // 1) get metadata (name + mime)
            var metaReq = drive.Files.Get(fileId);
            metaReq.Fields = "id,name,mimeType";
            var meta = await metaReq.ExecuteAsync();

            // 2) download content
            using var ms = new MemoryStream();
            var mediaReq = drive.Files.Get(fileId);
            await mediaReq.DownloadAsync(ms);

            return (ms.ToArray(), meta.Name ?? $"{fileId}", meta.MimeType ?? "application/octet-stream");
        }

        #region Edit file

        // Get the file content
        public async Task<(byte[] Content, string Name, string MimeType)> GetFileBytesAsync(TokenResponse token, string fileId, string name, string mimeType, string userKey = "current-user")
        {
            var drive = await CreateDriveServiceAsync(token, userKey);

            using var ms = new MemoryStream();
            var mediaReq = drive.Files.Get(fileId);
            mediaReq.SupportsAllDrives = true;
            await mediaReq.DownloadAsync(ms);

            return (ms.ToArray(), name, mimeType);
        }

        // Update the file content with new text
        public Task UpdateTextFileAsync(TokenResponse token, string fileId, string text, string? mimeType = "application/xml", string userKey = "current-user", string? expectedEtag = null)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(text ?? string.Empty);
            return UpdateFileContentAsync(token, fileId, bytes, mimeType ?? "text/plain", userKey, expectedEtag);
        }

        private async Task UpdateFileContentAsync(TokenResponse token, string fileId, byte[] newContent, string mimeType, string userKey = "current-user", string? expectedEtag = null)
        {
            var drive = await CreateDriveServiceAsync(token, userKey);

            using var stream = new MemoryStream(newContent);
            var update = drive.Files.Update(new Google.Apis.Drive.v3.Data.File(), fileId, stream, mimeType);
            update.SupportsAllDrives = true;

            var result = await update.UploadAsync();
            if (result.Status != UploadStatus.Completed)
                throw new InvalidOperationException($"Drive update failed: {result.Exception?.Message ?? result.Status.ToString()}");
        }
        #endregion

    }

    // Represents a file in Google Drive with relevant metadata
    public class GoogleFileModel
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string MimeType { get; set; }
        public string ViewLink { get; set; }
        public string Thumbnail { get; set; }
        public string FullPath { get; set; }
        public DateTimeOffset? ModifiedTime { get; set; }
    }
}
