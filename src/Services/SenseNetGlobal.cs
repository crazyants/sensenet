﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using System.Web.Routing;
using System.Configuration;
using System.Collections.Specialized;
using SenseNet.ContentRepository.Storage.Security;
using File = System.IO.File;
using SenseNet.ContentRepository;
using SenseNet.ContentRepository.Storage;
using SenseNet.Portal.Virtualization;
using System.IO;
using SenseNet.Diagnostics;
using SenseNet.Portal.Routing;
using SenseNet.Portal.Resources;
using SenseNet.Portal.Handlers;
using System.Web.Hosting;
using System.Web.SessionState;
using SenseNet.Portal;
using SenseNet.Tools;

namespace SenseNet.Services
{
    public class SenseNetGlobal
    {
        /*====================================================================================================================== Static part */
        private static SenseNetGlobal __instance;
        private static SenseNetGlobal Instance
        {
            get
            {
                if (__instance != null)
                    return __instance;
                
                var globalTypes = TypeResolver.GetTypesByBaseType(typeof(SenseNetGlobal));

                // Try to load the first custom type unknown to us. If not found, try to load the Sense/Net
                // global class, implemented in a higher layer by name.
                var globalType = globalTypes.FirstOrDefault(t => t.FullName != "SenseNet.Portal.SenseNetGlobal") ??
                                 TypeResolver.GetType("SenseNet.Portal.SenseNetGlobal", false);

                __instance = globalType != null ? (SenseNetGlobal)Activator.CreateInstance(globalType) : new SenseNetGlobal();

                SnTrace.System.Write("Global class loaded: " + __instance.GetType().FullName);

                return __instance;
            }
        }

        internal static void ApplicationStartHandler(object sender, EventArgs e, HttpApplication application)
        {
            Instance.Application_Start(sender, e, application);
        }
        internal static void ApplicationEndHandler(object sender, EventArgs e, HttpApplication application)
        {
            Instance.Application_End(sender, e, application);
        }
        internal static void ApplicationErrorHandler(object sender, EventArgs e, HttpApplication application)
        {
            Instance.Application_Error(sender, e, application);
        }
        internal static void ApplicationBeginRequestHandler(object sender, EventArgs e, HttpApplication application)
        {
            Instance.Application_BeginRequest(sender, e, application);
        }
        internal static void ApplicationEndRequestHandler(object sender, EventArgs e, HttpApplication application)
        {
            Instance.Application_EndRequest(sender, e, application);
        }

        /*====================================================================================================================== Instance part */
        internal static string RunOnceGuid = "101C50EF-24FD-441A-A15B-BD33DE431665";
        private static readonly int[] dontCareErrorCodes = new int[] { 401, 403, 404 };

        protected virtual void Application_Start(object sender, EventArgs e, HttpApplication application)
        {
            using (var op = SnTrace.Repository.StartOperation("Application_Start. Process: {0}, AppDomain: {1}, ",
                                System.Diagnostics.Process.GetCurrentProcess().Id, AppDomain.CurrentDomain.Id))
            {
                var runOnceMarkerPath = application.Server.MapPath("/" + RunOnceGuid);
                var firstRun = File.Exists(runOnceMarkerPath);
                var startConfig = new SenseNet.ContentRepository.RepositoryStartSettings { StartLuceneManager = !firstRun, IsWebContext = true };
                startConfig.ConfigureProvider(typeof(ElevatedModificationVisibilityRule), typeof(SnElevatedModificationVisibilityRule));

                RepositoryInstance.WaitForWriterLockFileIsReleased(RepositoryInstance.WaitForLockFileType.OnStart);

                Repository.Start(startConfig);

                StorageContext.L2Cache = new L2CacheImpl();

                RegisterRoutes(RouteTable.Routes, application);
                RepositoryPathProvider.Register();

                op.Successful = true;
            }
        }
        protected virtual void Application_End(object sender, EventArgs e, HttpApplication application)
        {
            using (var op = SnTrace.Repository.StartOperation("Application_End"))
            {
                SenseNet.ContentRepository.Repository.Shutdown();
                SnLog.WriteInformation("Application_End", EventId.RepositoryLifecycle, properties: new Dictionary<string, object> { 
                    { "ShutdownReason", HostingEnvironment.ShutdownReason  }
                });
                op.Successful = true;
            }
            SnTrace.Flush();
        }
        protected virtual void Application_Error(object sender, EventArgs e, HttpApplication application)
        {
            int? originalHttpCode = null;
            var ex = application.Server.GetLastError();

            var httpException = ex as HttpException;
            if (httpException != null)
                originalHttpCode = httpException.GetHttpCode();

            var unknownActionException = ex as UnknownActionException;
            if (unknownActionException != null)
            {
                SnTrace.Web.Write("UnknownActionException: " + unknownActionException.Message);
                originalHttpCode = 404;
            }

            // if httpcode is contained in the dontcare list (like 404), don't log the exception
            var skipLogException = originalHttpCode.HasValue && dontCareErrorCodes.Contains(originalHttpCode.Value);

            if (!skipLogException)
            {
                try
                {
                    SnLog.WriteException(ex);
                }
                catch
                {
                }
            }

            if (ex.InnerException != null && ex.InnerException.StackTrace != null &&
              (ex.InnerException.StackTrace.IndexOf("System.Web.UI.PageParser.GetCompiledPageInstanceInternal") != -1))
                return;

            if (HttpContext.Current == null)
                return;

            HttpResponse response;
            try
            {
                response = HttpContext.Current.Response;
            }
            catch (Exception)
            {
                response = null;
            }

            if (response != null)
                response.Headers.Remove("Content-Disposition");

            // HACK: HttpAction.cs (and possibly StaticFileHandler) throws 404 and 403 HttpExceptions. 
            // These are not exceptions to be displayed, but "fake" exceptions to handle 404 and 403 requests.
            // Therefore, here we set the statuscode and return, no further logic is executed.
            if (originalHttpCode.HasValue && (originalHttpCode == 404 || originalHttpCode == 403))
            {
                response.StatusCode = originalHttpCode.Value;

                HttpContext.Current.ClearError();
                HttpContext.Current.ApplicationInstance.CompleteRequest();
                return;
            }


            var errorPageHtml = string.Empty;

            var exception = ex;
            if (exception.InnerException != null) exception = exception.InnerException;

            var exceptionStatusCode = 0;
            var exceptionSubStatusCode = 0;
            var statusCodeExists = GetStatusCode(exception, out exceptionStatusCode, out exceptionSubStatusCode);

            if (response != null)
            {
                if (!HttpContext.Current.Request.Url.AbsoluteUri.StartsWith("http://localhost"))
                {
                    if (originalHttpCode.HasValue)
                        response.StatusCode = originalHttpCode.Value;

                    // If there is a specified status code in statusCodeString then set Response.StatusCode to it.
                    // Otherwise go on to global error page.
                    if (statusCodeExists)
                    {
                        application.Response.StatusCode = exceptionStatusCode;
                        application.Response.SubStatusCode = exceptionSubStatusCode;
                        response.Clear();
                        HttpContext.Current.ClearError();
                        HttpContext.Current.ApplicationInstance.CompleteRequest();
                        return;
                    }

                    application.Response.TrySkipIisCustomErrors = true; // keeps our custom error page defined below instead of using the page of IIS - works in IIS7 only

                    if (application.Response.StatusCode == 200)
                        application.Response.StatusCode = 500;

                    Node globalErrorNode = null;
                    var site = Site.Current;

                    if (site != null)
                    {
                        var path = string.Concat("/Root/System/ErrorMessages/", site.Name, "/UserGlobal.html");
                        globalErrorNode = SystemAccount.Execute(() => Node.LoadNode(path));
                    }

                    if (globalErrorNode != null)
                    {
                        var globalBinary = globalErrorNode.GetBinary("Binary");
                        var stream = globalBinary.GetStream();
                        if (stream != null)
                        {
                            var str = new StreamReader(stream);
                            errorPageHtml = str.ReadToEnd();
                        }
                    }
                    else
                    {
                        errorPageHtml = GetDefaultUserErrorPageHtml(application.Server.MapPath("/"), true);
                    }
                }
                else
                {
                    // if the page is requested from localhost
                    errorPageHtml = GetDefaultLocalErrorPageHtml(application.Server.MapPath("/"), true);
                }
            }
            else
            {
                // TODO: SQL Error handling
            }

            errorPageHtml = InsertErrorMessagesIntoHtml(exception, errorPageHtml);

            application.Response.TrySkipIisCustomErrors = true;

            // If there is a specified status code in statusCodeString then set Response.StatusCode to it.
            // Otherwise go on to global error page.
            if (statusCodeExists)
            {
                application.Response.StatusCode = exceptionStatusCode;
                application.Response.SubStatusCode = exceptionSubStatusCode;
                response.Clear();
                HttpContext.Current.ClearError();
                HttpContext.Current.ApplicationInstance.CompleteRequest();
            }
            else
            {
                if (application.Response.StatusCode == 200)
                    application.Response.StatusCode = 500;
            }

            if (response != null)
            {
                response.Clear();
                response.Write(errorPageHtml);
            }

            HttpContext.Current.ClearError();
            HttpContext.Current.ApplicationInstance.CompleteRequest();
        }
        protected virtual void Application_BeginRequest(object sender, EventArgs e, HttpApplication application)
        {
            HttpContext.Current.SetSessionStateBehavior(SessionStateBehavior.Required);
        }
        protected virtual void Application_EndRequest(object sender, EventArgs e, HttpApplication application)
        {
            if (PortalContext.Current == null)
                return;

            if (PortalContext.Current.IsOfficeProtocolRequest || PortalContext.Current.IsWebdavRequest)
            {
                // force 401, if formsauthentication module converted a 401 response to a 302 redirect to login page. we turn it back to 401
                var redirectingToLogin = HttpContext.Current.Response.StatusCode == 302 && HttpContext.Current.Response.RedirectLocation.ToLower().StartsWith(System.Web.Security.FormsAuthentication.LoginUrl);
                if (redirectingToLogin)
                {
                    HttpContext.Current.Response.RedirectLocation = null; // this is not any more a redirect
                    Portal.Dws.DwsHelper.CheckVisitor();
                }
            }
        }

        protected virtual void RegisterRoutes(RouteCollection routes, HttpApplication application)
        {
            routes.Add(new Route("odata.svc/{*path}", new ODataRouteHandler()));

            var resourceHandler = new ResourceHandler();
            routes.Add("SnResourceRoute", new Route(ResourceHandler.UrlPart + "/{*anything}", new ProxyingRouteHandler(ctx => resourceHandler)));
            routes.Add("SnBinaryRoute", new Route(BinaryHandlerBase.UrlPart + "/{contentId}/{propertyName}/{maxAge}/{width}/{height}", new RouteValueDictionary(new Dictionary<string, object>()
            {
                {"propertyName", "Binary"},
                {"maxAge", "0"},
                {"width", "0"},
                {"height", "0"},
            }), new ProxyingRouteHandler(ctx =>
            {
                var contentIdStr = ctx.RouteData.Values["contentId"] as string;
                int contentId = 0;
                if (!string.IsNullOrEmpty(contentIdStr) && !int.TryParse(contentIdStr, out contentId))
                    return null;

                var propertyName = ctx.RouteData.Values["propertyName"] as string ?? "Binary";

                var node = Node.LoadNode(contentId);

                if (node != null && propertyName != null && node.HasProperty(propertyName))
                {
                    var maxAgeStr = ctx.RouteData.Values["maxAge"] as string;
                    int maxAge;
                    int.TryParse(maxAgeStr, out maxAge);

                    var widthStr = ctx.RouteData.Values["width"] as string;
                    int width;
                    int.TryParse(widthStr, out width);

                    var heightStr = ctx.RouteData.Values["height"] as string;
                    int height;
                    int.TryParse(heightStr, out height);

                    var handler = new BinaryHandlerBase(node, propertyName, maxAge == 0 ? null : (TimeSpan?)TimeSpan.FromDays(maxAge), width == 0 ? null : (int?)width, height == 0 ? null : (int?)height);
                    return handler;
                }

                return null;
            })));
        }

        /*====================================================================================================================== Helpers */
        private static bool GetStatusCode(Exception exception, out int exceptionStatusCode, out int exceptionSubStatusCode)
        {
            exceptionStatusCode = 0;
            exceptionSubStatusCode = 0;

            var statusCodes = ConfigurationManager.GetSection("ExceptionStatusCodes") as NameValueCollection;
            if (statusCodes == null) return false;

            var tmpExceptionFullName = exception.GetType().FullName;
            var tmpException = exception.GetType();

            while (tmpExceptionFullName != "System.Exception")
            {
                if (tmpExceptionFullName != null)
                {
                    var statusCodeFullString = statusCodes[tmpExceptionFullName];
                    if (!string.IsNullOrEmpty(statusCodeFullString))
                    {
                        string statusCodeString;
                        string subStatusCodeString;

                        if (statusCodes[tmpExceptionFullName].Contains("."))
                        {
                            statusCodeString = statusCodeFullString.Split('.')[0];
                            subStatusCodeString = statusCodeFullString.Split('.')[1];
                        }
                        else
                        {
                            statusCodeString = statusCodeFullString;
                            subStatusCodeString = "0";
                        }

                        if (Int32.TryParse(statusCodeString, out exceptionStatusCode) && Int32.TryParse(subStatusCodeString, out exceptionSubStatusCode))
                            return true;
                        return false;
                    }

                    if (tmpException != null) tmpException = tmpException.BaseType;
                    if (tmpException != null) tmpExceptionFullName = tmpException.FullName;
                }

                return false;
            }

            return false;
        }
        private static string InsertErrorMessagesIntoHtml(Exception exception, string errorPageHtml)
        {
            errorPageHtml = errorPageHtml.Replace("{exceptionType}", exception.GetType().ToString());
            errorPageHtml = errorPageHtml.Replace("{exceptionMessage}", HttpUtility.HtmlEncode(exception.Message.Replace("\n", "<br />")));
            errorPageHtml = errorPageHtml.Replace("{exceptionToString}", HttpUtility.HtmlEncode(exception.ToString().Replace("\n", "<br />")));
            errorPageHtml = errorPageHtml.Replace("{exceptionSource}", exception.Source.ToString().Replace("\n", "<br />"));
            errorPageHtml = errorPageHtml.Replace("{exceptionStackTrace}", exception.StackTrace.ToString());

            var unknownActionExc = exception as UnknownActionException;
            if (unknownActionExc != null)
            {
                errorPageHtml = errorPageHtml.Replace("{exceptionActionName}", unknownActionExc.ActionName);
            }

            return errorPageHtml;
        }
        private static string GetDefaultUserErrorPageHtml(string serverPath, bool tryOnline)
        {
            return GetDefaultErrorPageHtml(serverPath, "UserGlobal.html", "UserErrorPage.html", tryOnline);
        }
        private static string GetDefaultLocalErrorPageHtml(string serverPath, bool tryOnline)
        {
            return GetDefaultErrorPageHtml(serverPath, "Global.html", "ErrorPage.html", tryOnline);
        }
        private static string GetDefaultErrorPageHtml(string serverMapPath, string page, string offlinePage, bool tryOnline)
        {
            Node global = null;

            if (tryOnline)
            {
                // Elevation: error message html should be 
                // independent from the current user.
                using (new SystemAccount())
                {
                    var site = Site.Current;

                    global = (site != null ? Node.LoadNode(string.Concat("/Root/System/ErrorMessages/", site.Name + "/", page)) : null) ??
                        Node.LoadNode(string.Concat("/Root/System/ErrorMessages/Default/", page));
                }
            }

            if (global != null)
            {
                var globalBinary = global.GetBinary("Binary");
                var stream = globalBinary.GetStream();
                if (stream != null)
                {
                    var str = new StreamReader(stream);
                    return str.ReadToEnd();
                }
            }
            else
            {
                try
                {
                    var path = string.Concat(serverMapPath, offlinePage);
                    using (var fs = File.Open(path, FileMode.Open, FileAccess.Read))
                    {
                        using (var sr = new StreamReader(fs))
                        {
                            return sr.ReadToEnd();
                        }
                    }
                }
                catch (Exception exc) // logged
                {
                    SnLog.WriteException(exc);
                }
            }

            return "<html><head><title>{exceptionType}</title></head><body style=\"font-family:Consolas, 'Courier New', Courier, monospace; background-color:#0033CC;color:#CCCCCC; font-weight:bold\"><br /><br /><br /><div style=\"text-align:center;background-color:#CCCCCC;color:#0033CC\">{exceptionType}</div><br /><br /><div style=\"font-size:large\">{exceptionMessage}<br /></div><br /><div style=\"font-size:x-small\">The source of the exception: {exceptionSource}</div><br /><div style=\"font-size:x-small\">Output of the Exception.ToString():<br />{exceptionToString}<br /><br /></div></body></html>";
        }

    }
}