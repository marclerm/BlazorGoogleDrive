# Google Drive Integration with Blazor

This project demonstrates how to integrate **Google Drive API** and **Google Drive Picker** into a Blazor application. It supports listing files, downloading, editing, and saving them back to Drive.

---

## 🔑 1. Enable Google Drive API & Credentials
1. Go to [Google Cloud Console](https://console.cloud.google.com).
2. Create a new project (or select an existing one).
3. Enable the **Google Drive API**.
4. Configure **OAuth 2.0 Client ID**:
   - Add `http://localhost:5001` (or your app’s URL) to the redirect URIs.
   - Download the OAuth credentials (client ID/secret).
5. Generate an **API Key** for Google Picker.

---

## 📦 2. Add NuGet Packages
Install the following NuGet packages:
```bash
dotnet add package Google.Apis.Drive.v3
dotnet add package Google.Apis.Auth
```

---

## ⚙️ 3. Configure `appsettings.json`
Store sensitive keys in `appsettings.json`:

```json
"GoogleAuth": {
  "ClientId": "YOUR_CLIENT_ID.apps.googleusercontent.com",
  "ClientSecret": "YOUR_CLIENT_SECRET",
  "ApiKey": "YOUR_DEVELOPER_API_KEY"
}
```

---

## 🔐 4. OAuth Flow in Blazor
Create a service (`GoogleOAuthService`) to handle login and token refresh. Example scope:

```csharp
private static readonly string[] DriveScopes = new[]
{
    Google.Apis.Drive.v3.DriveService.Scope.Drive
};
```

The scopes added here must match with the scopes assigned from "Data access" in the Google Cloud console project.

---

## 📂 5. Google Drive API Usage
Example service methods implemented:

- **List files**  
  Retrieves files with metadata (name, id, mime type, modified time).  

- **Download file**  
  Gets file bytes from Google Drive.  

- **Update file**  
  Pushes updates back to Drive with correct mime type.  

---

## 📑 6. Google Drive Picker Integration
1. Add the Google Picker API script:
   ```html
   <script type="text/javascript" src="https://apis.google.com/js/api.js"></script>
   ```
2. Implement a helper JS file (`googlePicker.js`):
   ```javascript
   window.showPicker = function (developerKey, oauthToken, callbackFunctionName) {
       if (oauthToken) {
          ...

           var picker = new google.picker.PickerBuilder()
               .setOAuthToken(oauthToken)
               .setDeveloperKey(developerKey)
               .addView(view)
               .setCallback(window[callbackFunctionName])
               .build();
           picker.setVisible(true);
       }
   };
   ```
3. Define a `[JSInvokable]` method in Blazor to receive the selected file:
   ```csharp
   [JSInvokable("OnDriveFilePicked")]
   public static void OnDriveFilePicked(string id, string name) 
   {
       Console.WriteLine($"Picked: {name} ({id})");
   }
   ```

---

## 💾 7. Save Flow with Feedback
After saving changes to Drive, notify the user:
```csharp
await JS.InvokeVoidAsync("alert", $"File '{_editingName}' saved successfully!");
```

---

## ✅ 8. Key Learnings
- Need **two keys**: OAuth **Access Token** + API **Developer Key** for Picker.
- Picker only shows files the user has access to and scopes must match (`Drive`).
- Refresh file list after saving to reflect timestamps and changes.
