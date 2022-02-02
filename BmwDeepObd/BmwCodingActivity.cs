﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Android.Content;
using Android.Content.PM;
using Android.Hardware.Usb;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.WebKit;
using Android.Webkit;
using AndroidX.AppCompat.App;
using AndroidX.Core.Content.PM;
using EdiabasLib;
using Java.Interop;

namespace BmwDeepObd
{
    [Android.App.Activity(Label = "@string/bmw_coding_title",
        Name = ActivityCommon.AppNameSpace + "." + nameof(BmwCodingActivity),
        LaunchMode = LaunchMode.SingleInstance,
        AlwaysRetainTaskState = true,
        WindowSoftInputMode = SoftInput.StateAlwaysHidden,
        ConfigurationChanges = ActivityConfigChanges)]
    public class BmwCodingActivity : BaseActivity
    {
        private class VehicleRequest
        {
            public enum VehicleRequestType
            {
                Connect,
                Disconnect,
                Transmit
            }

            public VehicleRequest(VehicleRequestType requestType, string id, string data = null)
            {
                RequestType = requestType;
                Id = id;
                Data = data;
            }

            public VehicleRequestType RequestType { get; }
            public string Id { get; }
            public string Data { get; }
        }

        public class InstanceData
        {
            public InstanceData()
            {
                Url = string.Empty;
            }

            public string Url { get; set; }
            public string TraceDir { get; set; }
            public bool TraceActive { get; set; }
            public bool CommErrorsOccured { get; set; }
        }

        public delegate void AcceptDelegate(bool accepted);

        private const int ConnectionTimeout = 6000;

        // Intent extra
        public const string ExtraEcuDir = "ecu_dir";
        public const string ExtraAppDataDir = "app_data_dir";
        public const string ExtraInterface = "interface";
        public const string ExtraDeviceName = "device_name";
        public const string ExtraDeviceAddress = "device_address";
        public const string ExtraEnetIp = "enet_ip";

#if DEBUG
        private static readonly string Tag = typeof(BmwCodingActivity).FullName;
#endif

        private enum ActivityRequest
        {
            RequestDummy,
        }

        private InstanceData _instanceData = new InstanceData();
        private bool _updateOptionsMenu;
        private ActivityCommon _activityCommon;
        private string _ecuDir;
        private string _appDataDir;
        private string _deviceName;
        private string _deviceAddress;
        private EdiabasNet _ediabas;
        private volatile bool _ediabasJobAbort;
        private Thread _ediabasThread;
        private AutoResetEvent _ediabasThreadWakeEvent = new AutoResetEvent(false);
        private object _ediabasLock = new object();
        private object _requestLock = new object();
        private object _timeLock = new object();
        private Queue<VehicleRequest> _requestQueue = new Queue<VehicleRequest>();
        private bool _activityActive;
        private bool _urlLoaded;
        private Timer _connectionCheckTimer;
        public long _connectionUpdateTime;

        private WebView _webViewCoding;

        protected override void OnCreate(Bundle savedInstanceState)
        {
            SetTheme(ActivityCommon.SelectedThemeId);
            base.OnCreate(savedInstanceState);
            _allowTitleHiding = false;

            if (savedInstanceState != null)
            {
                _instanceData = GetInstanceState(savedInstanceState, _instanceData) as InstanceData;
            }

            SupportActionBar.SetHomeButtonEnabled(true);
            SupportActionBar.SetDisplayShowHomeEnabled(true);
            SupportActionBar.SetDisplayHomeAsUpEnabled(true);
            SetContentView(Resource.Layout.bmw_coding);

            SetResult(Android.App.Result.Canceled);

            _activityCommon = new ActivityCommon(this, () =>
            {
                if (_activityActive)
                {
                    UpdateOptionsMenu();
                }
            }, BroadcastReceived);

            _ecuDir = Intent.GetStringExtra(ExtraEcuDir);
            _appDataDir = Intent.GetStringExtra(ExtraAppDataDir);
            _activityCommon.SelectedInterface = (ActivityCommon.InterfaceType)
                Intent.GetIntExtra(ExtraInterface, (int)ActivityCommon.InterfaceType.None);
            _deviceName = Intent.GetStringExtra(ExtraDeviceName);
            _deviceAddress = Intent.GetStringExtra(ExtraDeviceAddress);
            _activityCommon.SelectedEnetIp = Intent.GetStringExtra(ExtraEnetIp);

            _activityCommon.SetPreferredNetworkInterface();

            StartEdiabasThread();

            _webViewCoding = FindViewById<WebView>(Resource.Id.webViewCoding);
            try
            {
                WebSettings webSettings = _webViewCoding?.Settings;
                if (webSettings != null)
                {
                    webSettings.JavaScriptEnabled = true;
                    webSettings.JavaScriptCanOpenWindowsAutomatically = true;
                    webSettings.DomStorageEnabled = true;
                    webSettings.CacheMode = CacheModes.NoCache;

                    string userAgent = webSettings.UserAgentString;
                    if (!string.IsNullOrEmpty(userAgent))
                    {
                        PackageInfo packageInfo = PackageManager?.GetPackageInfo(PackageName ?? string.Empty, 0);
                        long packageVersion = -1;
                        if (packageInfo != null)
                        {
                            packageVersion = PackageInfoCompat.GetLongVersionCode(packageInfo);
                        }

                        string language = ActivityCommon.GetCurrentLanguage();
                        string userAgentAppend = string.Format(CultureInfo.InvariantCulture, " DeepObd/{0}/{1}", packageVersion, language);
                        userAgent += userAgentAppend;

                        webSettings.UserAgentString = userAgent;
                    }
                }

                _webViewCoding.AddJavascriptInterface(new WebViewJSInterface(this), "app");
                _webViewCoding.SetWebViewClient(new WebViewClientImpl(this));
                _webViewCoding.SetWebChromeClient(new WebChromeClientImpl(this));
            }
            catch (Exception)
            {
                // ignored
            }

            UpdateConnectTime();
            UpdateOptionsMenu();
        }

        protected override void OnSaveInstanceState(Bundle outState)
        {
            StoreInstanceState(outState, _instanceData);
            base.OnSaveInstanceState(outState);
        }

        protected override void OnResume()
        {
            base.OnResume();
            _activityActive = true;
            UpdateConnectTime();

            if (_connectionCheckTimer == null)
            {
                _connectionCheckTimer = new Timer(state =>
                {
                    RunOnUiThread(() =>
                    {
                        if (_activityCommon == null)
                        {
                            return;
                        }

                        LoadWebServerUrl();

                        if (!_urlLoaded)
                        {
                            return;
                        }

                        long connectTime = GetConnectTime();
                        if (Stopwatch.GetTimestamp() - connectTime >= ConnectionTimeout * ActivityCommon.TickResolMs)
                        {
                            try
                            {
                                UpdateConnectTime();
                                Toast.MakeText(this, GetString(Resource.String.bmw_coding_network_error), ToastLength.Short)?.Show();
                                if (_activityCommon.IsNetworkPresent(out _))
                                {
                                    _activityCommon.SetPreferredNetworkInterface();
                                    _webViewCoding.LoadUrl(_instanceData.Url);
                                }
                            }
                            catch (Exception)
                            {
                                // ignored
                            }
                        }
                    });
                }, null, 500, 500);
            }

        }

        protected override void OnPause()
        {
            base.OnPause();
            _activityActive = false;

            if (_connectionCheckTimer != null)
            {
                _connectionCheckTimer.Dispose();
                _connectionCheckTimer = null;
            }
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();

            StopEdiabasThread();

            _activityCommon?.Dispose();
            _activityCommon = null;
        }

        public override void OnBackPressed()
        {
            ConnectionActiveWarn(accepted =>
            {
                if (_activityCommon == null)
                {
                    return;
                }

                if (accepted)
                {
                    OnBackPressedContinue();
                }
            });
        }

        private void OnBackPressedContinue()
        {
            if (!SendTraceFile((sender, args) =>
                {
                    if (_activityCommon == null)
                    {
                        return;
                    }
                    base.OnBackPressed();
                }))
            {
                base.OnBackPressed();
            }
        }

        public override bool OnOptionsItemSelected(IMenuItem item)
        {
            switch (item.ItemId)
            {
                case Android.Resource.Id.Home:
                    ConnectionActiveWarn(accepted =>
                    {
                        if (_activityCommon == null)
                        {
                            return;
                        }

                        if (accepted)
                        {
                            FinishContinue();
                        }
                    });
                    return true;

                case Resource.Id.menu_send_trace:
                    if (IsEdiabasConnected())
                    {
                        return true;
                    }
                    SendTraceFileAlways((sender, args) =>
                    {
                        if (_activityCommon == null)
                        {
                            return;
                        }
                        UpdateOptionsMenu();
                    });
                    return true;
            }

            return base.OnOptionsItemSelected(item);
        }

        private void FinishContinue()
        {
            if (!SendTraceFile((sender, args) =>
                {
                    if (_activityCommon == null)
                    {
                        return;
                    }
                    Finish();
                }))
            {
                Finish();
            }
        }

        protected override void OnActivityResult(int requestCode, Android.App.Result resultCode, Intent data)
        {
            switch ((ActivityRequest)requestCode)
            {
                case ActivityRequest.RequestDummy:
                    UpdateOptionsMenu();
                    break;
            }
        }

        public override bool OnCreateOptionsMenu(IMenu menu)
        {
            var inflater = MenuInflater;
            inflater.Inflate(Resource.Menu.bmw_coding_menu, menu);
            return true;
        }

        public override bool OnMenuOpened(int featureId, IMenu menu)
        {
            if (_updateOptionsMenu)
            {
                _updateOptionsMenu = false;
                OnPrepareOptionsMenu(menu);
            }
            return base.OnMenuOpened(featureId, menu);
        }

        public override bool OnPrepareOptionsMenu(IMenu menu)
        {
            bool commActive = IsEdiabasConnected();
            bool interfaceAvailable = _activityCommon.IsInterfaceAvailable();

            IMenuItem sendTraceMenu = menu.FindItem(Resource.Id.menu_send_trace);
            sendTraceMenu?.SetEnabled(interfaceAvailable && !commActive && _instanceData.TraceActive && ActivityCommon.IsTraceFilePresent(_instanceData.TraceDir));

            return base.OnPrepareOptionsMenu(menu);
        }

        private void ConnectionActiveWarn(AcceptDelegate handler)
        {
            if (!IsEdiabasConnected())
            {
                handler.Invoke(true);
                return;
            }

            new AlertDialog.Builder(this)
                .SetPositiveButton(Resource.String.button_yes, (sender, args) =>
                {
                    _instanceData.CommErrorsOccured = true;
                    handler(true);
                })
                .SetNegativeButton(Resource.String.button_no, (sender, args) =>
                {
                    handler(false);
                })
                .SetMessage(Resource.String.bmw_coding_connection_active)
                .SetTitle(Resource.String.alert_title_warning)
                .Show();
        }

        private void UpdateOptionsMenu()
        {
            _updateOptionsMenu = true;
        }

        private void SendVehicleResponseThread(string id, string response)
        {
            RunOnUiThread(() =>
            {
                if (_activityCommon == null)
                {
                    return;
                }

                SendVehicleResponse(id, response);
            });
        }

        private void LoadWebServerUrl()
        {
            if (_urlLoaded)
            {
                return;
            }

            if (!_activityCommon.IsNetworkPresent(out string domains))
            {
                return;
            }

            try
            {
                if (string.IsNullOrEmpty(_instanceData.Url))
                {
                    if (!string.IsNullOrEmpty(domains) && domains.Contains("local.holeschak.de", StringComparison.OrdinalIgnoreCase))
                    {
                        _instanceData.Url = @"http://ulrich3.local.holeschak.de:3000";
                    }
                    else
                    {
                        //_instanceData.Url = @"http://holeschak.dedyn.io:3000";
                        _instanceData.Url = @"http://holeschak.dedyn.io:8008";
                    }
                }

                _activityCommon.SetPreferredNetworkInterface();
                _webViewCoding.LoadUrl(_instanceData.Url);
                _urlLoaded = true;
            }
            catch (Exception)
            {
                // ignored
            }
        }

        private bool SendVehicleResponse(string id, string response)
        {
            try
            {
                _activityCommon.SetPreferredNetworkInterface();
                string script = string.Format(CultureInfo.InvariantCulture, "sendVehicleResponse(`{0}`, `{1}`);", id, response);
                if (Build.VERSION.SdkInt < BuildVersionCodes.Kitkat)
                {
                    _webViewCoding.LoadUrl("javascript:" + script);
                }
                else
                {
                    _webViewCoding.EvaluateJavascript(script, new VehicleSendCallback());
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void ReloadPage()
        {
            RunOnUiThread(() =>
            {
                if (_activityCommon == null)
                {
                    return;
                }

                try
                {
                    _webViewCoding.Reload();
                }
                catch (Exception)
                {
                    // ignored
                }
            });
        }

        private void BroadcastReceived(Context context, Intent intent)
        {
            if (intent == null)
            {   // from usb check timer
                if (_activityActive)
                {
                    _activityCommon.RequestUsbPermission(null);
                }
                return;
            }
            string action = intent.Action;
            switch (action)
            {
                case UsbManager.ActionUsbDeviceAttached:
                    if (_activityActive)
                    {
                        if (intent.GetParcelableExtra(UsbManager.ExtraDevice) is UsbDevice usbDevice)
                        {
                            _activityCommon.RequestUsbPermission(usbDevice);
                            UpdateOptionsMenu();
                        }
                    }
                    break;
            }
        }

        private string EnqueueVehicleRequest(VehicleRequest vehicleRequest)
        {
            UpdateConnectTime();

            lock (_requestLock)
            {
                if (_requestQueue.Count > 0)
                {
                    return "Request queue full";
                }

                _requestQueue.Enqueue(vehicleRequest);
                _ediabasThreadWakeEvent.Set();
            }

            return string.Empty;
        }

        private EdiabasNet EdiabasSetup()
        {
            EdiabasNet ediabas = new EdiabasNet
            {
                EdInterfaceClass = _activityCommon.GetEdiabasInterfaceClass(),
                AbortJobFunc = AbortEdiabasJob
            };
            ediabas.SetConfigProperty("EcuPath", _ecuDir);
            _instanceData.TraceDir = Path.Combine(_appDataDir, "LogBmwCoding");
            if (!string.IsNullOrEmpty(_instanceData.TraceDir))
            {
                _instanceData.TraceActive = true;
                ediabas.SetConfigProperty("TracePath", _instanceData.TraceDir);
                ediabas.SetConfigProperty("IfhTrace", string.Format("{0}", (int)EdiabasNet.EdLogLevel.Error));
                ediabas.SetConfigProperty("CompressTrace", "1");
            }
            else
            {
                _instanceData.TraceActive = false;
                ediabas.SetConfigProperty("IfhTrace", "0");
            }

            _activityCommon.SetEdiabasInterface(ediabas, _deviceAddress);

            return ediabas;
        }

        private bool StartEdiabasThread()
        {
            if (IsEdiabasThreadRunning())
            {
                return true;
            }

            if (_ediabas == null)
            {
                _ediabas = EdiabasSetup();
            }

            _ediabasJobAbort = false;
            _ediabasThreadWakeEvent.Reset();
            _ediabasThread = new Thread(EdiabasThread);
            _ediabasThread.Start();
            UpdateOptionsMenu();

            return true;
        }

        private bool StopEdiabasThread()
        {
            _ediabasJobAbort = true;
            _ediabasThreadWakeEvent.Set();
            if (IsEdiabasThreadRunning())
            {
                _ediabasThread.Join();
            }

            lock (_ediabasLock)
            {
                if (_ediabas != null)
                {
                    _ediabas.Dispose();
                    _ediabas = null;
                }
            }
            UpdateOptionsMenu();

            return true;
        }

        private bool IsEdiabasThreadRunning()
        {
            if (_ediabasThread == null)
            {
                return false;
            }
            if (_ediabasThread.IsAlive)
            {
                return true;
            }
            _ediabasThread = null;
            return false;
        }

        private bool AbortEdiabasJob()
        {
            if (_ediabasJobAbort)
            {
                return true;
            }
            return false;
        }

        public bool EdiabasConnect()
        {
            lock (_ediabasLock)
            {
                if (_ediabas == null)
                {
                    return false;
                }

                try
                {
                    if (_ediabas.EdInterfaceClass.InterfaceConnect())
                    {
                        _ediabas.LogString(EdiabasNet.EdLogLevel.Ifh, "Ediabas connected");
                        return true;
                    }

                    _ediabas.LogString(EdiabasNet.EdLogLevel.Ifh, "Ediabas connect failed");
                    return false;
                }
                catch (Exception ex)
                {
                    _ediabas.LogFormat(EdiabasNet.EdLogLevel.Ifh, "Ediabas connect Exception: {0}", EdiabasNet.GetExceptionText(ex));
                    return false;
                }
            }
        }

        public bool EdiabasDisconnect()
        {
            lock (_ediabasLock)
            {
                if (_ediabas == null)
                {
                    return false;
                }

                try
                {
                    _ediabas.LogString(EdiabasNet.EdLogLevel.Ifh, "Ediabas disconnect");
                    return _ediabas.EdInterfaceClass.InterfaceDisconnect();
                }
                catch (Exception ex)
                {
                    _ediabas.LogFormat(EdiabasNet.EdLogLevel.Ifh, "Ediabas disconnect Exception: {0}", EdiabasNet.GetExceptionText(ex));
                    return false;
                }
            }
        }

        public bool IsEdiabasConnected()
        {
            lock (_ediabasLock)
            {
                if (_ediabas == null)
                {
                    return false;
                }

                return _ediabas.EdInterfaceClass.Connected;
            }
        }

        public List<byte[]> EdiabasTransmit(byte[] requestData)
        {
            List<byte[]> responseList = new List<byte[]>();
            if (requestData == null || requestData.Length < 3)
            {
                return responseList;
            }

            lock (_ediabasLock)
            {
                byte[] sendData = requestData;
                bool funcAddress = (sendData[0] & 0xC0) == 0xC0;     // functional address

                _ediabas.LogFormat(EdiabasNet.EdLogLevel.Ifh, "Ediabas transmit, Func={0}", funcAddress);

                for (; ; )
                {
                    bool dataReceived = false;

                    if (_ediabas == null)
                    {
                        break;
                    }

                    try
                    {
                        if (_ediabas.EdInterfaceClass.TransmitData(sendData, out byte[] receiveData))
                        {
                            responseList.Add(receiveData);
                            dataReceived = true;
                        }
                        else
                        {
                            if (!funcAddress)
                            {
                                _ediabas.LogFormat(EdiabasNet.EdLogLevel.Ifh, "*** No response");
                            }
                        }
                    }
                    catch (Exception)
                    {
                    }

                    if (!funcAddress || !dataReceived)
                    {
                        break;
                    }

                    if (AbortEdiabasJob())
                    {
                        break;
                    }

                    sendData = Array.Empty<byte>();
                }
            }

            return responseList;
        }

        private void EdiabasThread()
        {
            for (;;)
            {
                _ediabasThreadWakeEvent.WaitOne(100);
                if (_ediabasJobAbort)
                {
                    break;
                }

                VehicleRequest vehicleRequest = null;
                lock (_requestLock)
                {
                    if (!_requestQueue.TryDequeue(out vehicleRequest))
                    {
                        vehicleRequest = null;
                    }
                }

                if (vehicleRequest != null)
                {
                    StringBuilder sbBody = new StringBuilder();
                    sbBody.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n");
                    sbBody.Append("<vehicle_info>\r\n");

                    bool valid = true;
                    switch (vehicleRequest.RequestType)
                    {
                        case VehicleRequest.VehicleRequestType.Connect:
                        {
                            EdiabasDisconnect();
                            bool isConnected = EdiabasConnect();
                            RunOnUiThread(() =>
                            {
                                if (_activityCommon == null)
                                {
                                    return;
                                }

                                if (!isConnected)
                                {
                                    _instanceData.CommErrorsOccured = true;
                                }
                                _activityCommon.SetLock(ActivityCommon.LockType.ScreenDim);
                                UpdateOptionsMenu();
                            });
                            break;
                        }

                        case VehicleRequest.VehicleRequestType.Disconnect:
                            EdiabasDisconnect();
                            RunOnUiThread(() =>
                            {
                                if (_activityCommon == null)
                                {
                                    return;
                                }

                                _activityCommon.SetLock(ActivityCommon.LockType.None);
                                UpdateOptionsMenu();
                            });
                            break;

                        case VehicleRequest.VehicleRequestType.Transmit:
                        {
                            if (string.IsNullOrEmpty(vehicleRequest.Data))
                            {
                                valid = false;
                                break;
                            }

                            string requestString = vehicleRequest.Data.Replace(" ", "");
                            byte[] requestData = EdiabasNet.HexToByteArray(requestString);
                            sbBody.Append($" <data request=\"{System.Web.HttpUtility.HtmlEncode(requestString)}\" />\r\n");
                            List<byte[]> responseList = EdiabasTransmit(requestData);
                            foreach (byte[] responseData in responseList)
                            {
                                string responseReport = BitConverter.ToString(responseData).Replace("-", "");
                                sbBody.Append($" <data response=\"{System.Web.HttpUtility.HtmlEncode(responseReport)}\" />\r\n");
                            }
                            break;
                        }
                    }

                    string validReport = valid ? "1" : "0";
                    string idReport = vehicleRequest.Id ?? string.Empty;
                    sbBody.Append($" <request valid=\"{System.Web.HttpUtility.HtmlEncode(validReport)}\" id=\"{System.Web.HttpUtility.HtmlEncode(idReport)}\" />\r\n");

                    bool connected = IsEdiabasConnected();
                    string connectedState = connected ? "1" : "0";

                    sbBody.Append($" <status connected=\"{System.Web.HttpUtility.HtmlEncode(connectedState)}\"");
                    if (vehicleRequest.RequestType == VehicleRequest.VehicleRequestType.Connect)
                    {
                        string appIdState = ActivityCommon.AppId ?? string.Empty;
                        string adapterSerialState = ActivityCommon.LastAdapterSerial ?? string.Empty;
                        sbBody.Append($" app_id=\"{System.Web.HttpUtility.HtmlEncode(appIdState)}\"");
                        sbBody.Append($" adapter_serial=\"{System.Web.HttpUtility.HtmlEncode(adapterSerialState)}\"");
                    }
                    sbBody.Append(" />\r\n");
                    sbBody.Append("</vehicle_info>\r\n");

                    SendVehicleResponseThread(vehicleRequest.Id, sbBody.ToString());
                }
            }
        }

        private bool SendTraceFile(EventHandler<EventArgs> handler)
        {
            if (_instanceData.CommErrorsOccured && _instanceData.TraceActive && !string.IsNullOrEmpty(_instanceData.TraceDir))
            {
                if (!StopEdiabasThread())
                {
                    return false;
                }
                return _activityCommon.RequestSendTraceFile(_appDataDir, _instanceData.TraceDir, GetType(), handler);
            }
            return false;
        }

        private bool SendTraceFileAlways(EventHandler<EventArgs> handler)
        {
            if (IsEdiabasConnected())
            {
                return false;
            }

            if (ActivityCommon.CollectDebugInfo ||
                (_instanceData.TraceActive && !string.IsNullOrEmpty(_instanceData.TraceDir)))
            {
                if (!StopEdiabasThread())
                {
                    return false;
                }
                return _activityCommon.SendTraceFile(_appDataDir, _instanceData.TraceDir, GetType(), handler);
            }
            return false;
        }

        private void ReportError(string msg)
        {
            RunOnUiThread(() =>
            {
                if (_activityCommon == null)
                {
                    return;
                }

                _instanceData.CommErrorsOccured = true;

                lock (_ediabasLock)
                {
                    _ediabas?.LogFormat(EdiabasNet.EdLogLevel.Ifh, "ReportError: {0}", msg);
                }
            });
        }

        private void UpdateConnectTime(bool reset = false)
        {
            lock (_timeLock)
            {
                _connectionUpdateTime = reset ? DateTime.MinValue.Ticks : Stopwatch.GetTimestamp();
            }
        }

        private long GetConnectTime()
        {
            lock (_timeLock)
            {
                return _connectionUpdateTime;
            }
        }

        private class WebViewClientImpl : WebViewClientCompat
        {
            private BmwCodingActivity _activity;

            public WebViewClientImpl(BmwCodingActivity activity)
            {
                _activity = activity;
            }

            public override bool ShouldOverrideUrlLoading(WebView view, IWebResourceRequest request)
            {
                return false;
            }

            public override void OnPageFinished(WebView view, string url)
            {
                _activity.UpdateConnectTime();
            }

            public override void OnReceivedError(WebView view, IWebResourceRequest request, WebResourceErrorCompat error)
            {
                _activity.UpdateConnectTime(true);
            }

            public override void DoUpdateVisitedHistory(WebView view, string url, bool isReload)
            {
#if DEBUG
                Android.Util.Log.Debug(Tag, string.Format("DoUpdateVisitedHistory: Url={0}, Reload={1}", url, isReload));
#endif
                if (!string.IsNullOrEmpty(url))
                {
                    _activity._instanceData.Url = url;
                }

                base.DoUpdateVisitedHistory(view, url, isReload);
            }
        }

        private class WebChromeClientImpl : WebChromeClient
        {
            private BmwCodingActivity _activity;

            public WebChromeClientImpl(BmwCodingActivity activity)
            {
                _activity = activity;
            }

            public override bool OnConsoleMessage(ConsoleMessage consoleMessage)
            {
#if DEBUG
                string message = string.Format(CultureInfo.InvariantCulture, "Message: {0}, Line: {1}", consoleMessage.Message(), consoleMessage.LineNumber());
                Android.Util.Log.Debug(Tag, message);
#endif
                return true;
            }
        }

        private class VehicleSendCallback : Java.Lang.Object, IValueCallback
        {
            public void OnReceiveValue(Java.Lang.Object value)
            {
#if DEBUG
                Android.Util.Log.Debug(Tag, string.Format("VehicleSendCallback: {0}", value));
#endif
            }
        }

        private class WebViewJSInterface : Java.Lang.Object
        {
            BmwCodingActivity _activity;

            public WebViewJSInterface(BmwCodingActivity activity)
            {
                _activity = activity;
            }

            [JavascriptInterface]
            [Export]
            public string VehicleConnect(string id)
            {
#if DEBUG
                Android.Util.Log.Debug(Tag, string.Format("VehicleConnect: Id={0}", id));
#endif
                return _activity.EnqueueVehicleRequest(new VehicleRequest(VehicleRequest.VehicleRequestType.Connect, id));
            }

            [JavascriptInterface]
            [Export]
            public string VehicleDisconnect(string id)
            {
#if DEBUG
                Android.Util.Log.Debug(Tag, string.Format("VehicleDisconnect: Id={0}", id));
#endif
                return _activity.EnqueueVehicleRequest(new VehicleRequest(VehicleRequest.VehicleRequestType.Disconnect, id));
            }

            [JavascriptInterface]
            [Export]
            public string VehicleSend(string id, string data)
            {
#if DEBUG
                Android.Util.Log.Debug(Tag, string.Format("VehicleSend: Id={0}, Data={1}", id, data));
#endif
                return _activity.EnqueueVehicleRequest(new VehicleRequest(VehicleRequest.VehicleRequestType.Transmit, id, data));
            }

            [JavascriptInterface]
            [Export]
            public void ReportError(string msg)
            {
#if DEBUG
                Android.Util.Log.Debug(Tag, string.Format("ReportError: Msg={0}", msg));
#endif
                _activity.ReportError(msg);
            }

            [JavascriptInterface]
            [Export]
            public void UpdateStatus(bool status)
            {
#if DEBUG
                Android.Util.Log.Debug(Tag, string.Format("UpdateStatus: Status={0}", status));
#endif
                _activity.UpdateConnectTime();
            }

            [JavascriptInterface]
            [Export]
            public void ReloadPage()
            {
#if DEBUG
                Android.Util.Log.Debug(Tag, "ReloadPage");
#endif
                _activity.ReloadPage();
            }
        }
    }
}