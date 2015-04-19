﻿using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using System.Windows.Forms;

namespace Jackett
{
    public class Server
    {
        int Port = 9117;

        HttpListener listener;
        IndexerManager indexerManager;
        WebApi webApi;
        SonarrApi sonarrApi;

        public Server()
        {
            LoadApiKey();

            indexerManager = new IndexerManager();
            sonarrApi = new SonarrApi();
            webApi = new WebApi(indexerManager, sonarrApi);

            listener = new HttpListener();
            listener.Prefixes.Add("http://*:9117/");
        }

        void LoadApiKey()
        {
            var apiKeyFile = Path.Combine(Program.AppConfigDirectory, "api_key.txt");
            if (File.Exists(apiKeyFile))
                ApiKey.CurrentKey = File.ReadAllText(apiKeyFile).Trim();
            else
            {
                ApiKey.CurrentKey = ApiKey.Generate();
                File.WriteAllText(apiKeyFile, ApiKey.CurrentKey);
            }
        }

        public async void Start()
        {
            Program.LoggerInstance.Info("Starting HTTP server...");

            try
            {
                listener.Start();
                Process.Start("http://127.0.0.1:" + Port);
            }
            catch (HttpListenerException ex)
            {
                var dialogResult = MessageBox.Show(
                    "App must be ran as Admin for permission to use port " + Port + Environment.NewLine + "Restart app with admin privileges?",
                    "Failed to open port",
                    MessageBoxButtons.YesNo
                );
                if (dialogResult == DialogResult.No)
                {
                    Program.LoggerInstance.FatalException("App must be ran as Admin for permission to use port " + Port, ex);
                    Application.Exit();
                    return;
                }
                else
                {
                    Program.RestartAsAdmin();
                }
            }
            catch (Exception ex)
            {
                Program.LoggerInstance.ErrorException("Error: " + ex.Message, ex);
                return;
            }
            Program.LoggerInstance.Info("Server started on port " + Port);
            while (true)
            {
                var context = await listener.GetContextAsync();
                ProcessHttpRequest(context);
            }
        }

        public void Stop()
        {
            listener.Stop();
            listener.Abort();
        }

        async void ProcessHttpRequest(HttpListenerContext context)
        {
            Program.LoggerInstance.Trace("Received request: " + context.Request.Url.ToString());
            Exception exception = null;
            try
            {
                if (await webApi.HandleRequest(context))
                {

                }
                else if (context.Request.Url.AbsolutePath.StartsWith("/api/"))
                {
                    await ProcessTorznab(context);
                }
                else
                {
                    var responseBytes = Encoding.UTF8.GetBytes("Invalid request");
                    await context.Response.OutputStream.WriteAsync(responseBytes, 0, responseBytes.Length);
                }
            }
            catch (Exception ex)
            {
                exception = ex;
            }

            if (exception != null)
            {
                try
                {
                    var errorBytes = Encoding.UTF8.GetBytes(exception.Message);
                    await context.Response.OutputStream.WriteAsync(errorBytes, 0, errorBytes.Length);
                }
                catch (Exception) { }
            }

            context.Response.Close();

        }

        async Task ProcessTorznab(HttpListenerContext context)
        {

            var query = HttpUtility.ParseQueryString(context.Request.Url.Query);
            var inputStream = context.Request.InputStream;
            var reader = new StreamReader(inputStream, context.Request.ContentEncoding);
            var bytes = await reader.ReadToEndAsync();

            var indexerId = context.Request.Url.Segments[2].TrimEnd('/').ToLower();
            var indexer = indexerManager.GetIndexer(indexerId);

            if (context.Request.Url.Segments.Length > 4 && context.Request.Url.Segments[3] == "download/")
            {
                var downloadSegment = HttpServerUtility.UrlTokenDecode(context.Request.Url.Segments[4].TrimEnd('/'));
                var downloadLink = Encoding.UTF8.GetString(downloadSegment);
                var downloadBytes = await indexer.Download(new Uri(downloadLink));
                await context.Response.OutputStream.WriteAsync(downloadBytes, 0, downloadBytes.Length);
                return;
            }

            var torznabQuery = TorznabQuery.FromHttpQuery(query);

            torznabQuery.ShowTitles = await sonarrApi.GetShowTitle(torznabQuery.RageID);

            var releases = await indexer.PerformQuery(torznabQuery);

            var severUrl = string.Format("{0}://{1}:{2}/", context.Request.Url.Scheme, context.Request.Url.Host, context.Request.Url.Port);

            var resultPage = new ResultPage(new ChannelInfo
            {
                Title = indexer.DisplayName,
                Description = indexer.DisplayDescription,
                Link = indexer.SiteLink,
                ImageUrl = new Uri(severUrl + "logos/" + indexerId + ".png"),
                ImageTitle = indexer.DisplayName,
                ImageLink = indexer.SiteLink,
                ImageDescription = indexer.DisplayName
            });

            // add Jackett proxy to download links...
            foreach (var release in releases)
            {
                if (release.Link == null || release.Link.Scheme == "magnet")
                    continue;
                var originalLink = release.Link;
                var encodedLink = HttpServerUtility.UrlTokenEncode(Encoding.UTF8.GetBytes(originalLink.ToString())) + "/download.torrent";
                var proxyLink = string.Format("{0}api/{1}/download/{2}", severUrl, indexerId, encodedLink);
                release.Link = new Uri(proxyLink);
            }

            resultPage.Releases.AddRange(releases);

            var xml = resultPage.ToXml(new Uri(severUrl));

            var responseBytes = Encoding.UTF8.GetBytes(xml);
            context.Response.ContentEncoding = Encoding.UTF8;
            context.Response.ContentLength64 = responseBytes.LongLength;
            context.Response.ContentType = "application/rss+xml";
            await context.Response.OutputStream.WriteAsync(responseBytes, 0, responseBytes.Length);

        }



    }
}
