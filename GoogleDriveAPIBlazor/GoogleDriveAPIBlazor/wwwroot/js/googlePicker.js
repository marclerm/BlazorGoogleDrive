
var oauthToken;
var pickerReady = false;

window.onGapiLoad = function () {
    // Load the 'picker' module into the google namespace
    gapi.load('picker', {
        callback: function () {
            pickerReady = true;
            console.log("Google Picker module ready");
        }
    });
}

window.showPicker = function (developerKey, oauthToken, callbackFunctionName) {
    if (pickerReady && oauthToken) {
        var docsView = new google.picker.DocsView()
            .setParent('root')
            .setIncludeFolders(true)
            .setMode(google.picker.DocsViewMode.LIST);

        var picker = new google.picker.PickerBuilder()
            .enableFeature(google.picker.Feature.NAV_HIDDEN)
            .enableFeature(google.picker.Feature.MULTISELECT_ENABLED)
            .setOAuthToken(oauthToken)
            .setDeveloperKey(developerKey)
            .addView(docsView)
            .setCallback(window[callbackFunctionName])
            .build();
        picker.setVisible(true);
    }
}

window.onPicked = function (data) {
    if (data[google.picker.Response.ACTION] == google.picker.Action.PICKED) {
        var doc = data[google.picker.Response.DOCUMENTS][0];
        const name = doc[google.picker.Document.NAME] || "";
        const mimeType = doc[google.picker.Document.MIME_TYPE] || "";

        if (name.endsWith(".xml")  || name.endsWith(".txt") || name.endsWith(".json")) {
            console.log(`Valid file selected: Name = ${name}, Document ID = ${doc.id}, MIME Type = ${mimeType}`);
            // TODO: Call a C# method to handle the picked file
            window._pickerRef.invokeMethodAsync('OnDriveFilePicked', doc.id, name, mimeType);
        } else {
            alert("Only XML, TXT or JSON files are allowed.");
        }
    }
}

// Store the .NET reference to call the C# picker method from JavaScript based on registration
window.picker_registerDotNetRef = function (dotNetRef) {
    window._pickerRef = dotNetRef;
};

