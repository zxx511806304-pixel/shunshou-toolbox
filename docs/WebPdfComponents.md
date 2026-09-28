# Webpage PDF component

The webpage-to-PDF workspace uses the application's existing Microsoft WebView2 SDK (1.0.3179.45, brought in by Windows App SDK 1.8). It uses the installed Microsoft Edge WebView2 Evergreen Runtime; it does not bundle another Chromium browser or use a PDF conversion cloud service. The runtime is maintained by Microsoft and its installed version can differ between computers. When it is absent the workspace offers Microsoft's official installation page.

Web pages use a per-instance temporary user-data directory and an InPrivate controller. Existing Chrome/Edge cookies, passwords and browser profiles are not read. No host object or local-file API is exposed to the page. Only HTTP(S) top-level URLs without embedded credentials are accepted. New windows, downloads, external URI launches and privileged permission requests are blocked. Browser security protections remain enabled. Loaded websites still make their normal network requests.

The user sees the live webpage before exporting, then the WebView2 printing engine writes an A4 PDF locally. Print background graphics are retained and text remains selectable when provided by the webpage. The website's print CSS governs pagination; lazy content must be loaded in the preview before saving. Output filenames derive from the page title, are sanitized, and never overwrite existing files.

Official sources:

- [WebView2 for WinUI 3 and custom environments](https://learn.microsoft.com/en-us/microsoft-edge/webview2/platforms/winui3-windows-app-sdk)
- [CoreWebView2 PrintToPdfAsync](https://learn.microsoft.com/en-us/microsoft-edge/webview2/reference/winrt/microsoft_web_webview2_core/corewebview2#printtopdfasync)
- [Print settings and dimensions](https://learn.microsoft.com/en-us/microsoft-edge/webview2/reference/winrt/microsoft_web_webview2_core/corewebview2printsettings)
- [WebView2 distribution](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution)
- [Process failure handling and automatic GPU recovery](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/process-related-events)
- [Microsoft WebView2 official download](https://developer.microsoft.com/microsoft-edge/webview2/)
- [WebView2 samples and SDK license](https://github.com/MicrosoftEdge/WebView2Samples)

The deterministic release check serves a three-page print document over loopback HTTP and goes through the same load/save operations as the UI. It verifies selectable text on all pages, A4 dimensions, duplicate-save preservation, HTTP error handling, invalid schemes/URL credentials, private-profile isolation, cancellation/recovery, and captures the actual WebView rendering separately from the WinUI chrome.
