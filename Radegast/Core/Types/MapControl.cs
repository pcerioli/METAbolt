﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Data;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Windows.Forms;
using System.Net;
using System.Text.RegularExpressions;
using System.IO;
using System.Threading;
using OpenMetaverse;
using OpenMetaverse.Http;
using OpenMetaverse.Assets;

namespace Radegast
{
    public partial class MapControl : UserControl
    {
        RadegastInstance Instance;
        GridClient Client { get { return Instance.Client; } }
        ParallelDownloader downloader;
        Color background;
        float zoom;
        Font textFont;
        Brush textBrush;
        Brush textBackgroudBrush;
        uint regionSize = 256;
        float pixelsPerMeter;
        double centerX, centerY, targetX, targetY;
        GridRegion targetRegion, nullRegion;
        bool centered = false;
        int PixRegS;
        float maxZoom = 6f, minZoom = 0.5f;

        public bool UseExternalTiles = false;
        public event EventHandler<MapTargetChangedEventArgs> MapTargetChanged;
        public event EventHandler<EventArgs> ZoomChanged;
        public float MaxZoom { get { return maxZoom; } }
        public float MinZoom { get { return minZoom; } }

        public MapControl(RadegastInstance instance)
        {
            Zoom = 1.25f;
            InitializeComponent();
            Disposed += new EventHandler(MapControl_Disposed);
            this.Instance = instance;

            downloader = new ParallelDownloader();

            background = Color.FromArgb(4, 4, 75);
            textFont = new Font(FontFamily.GenericSansSerif, 8.0f, FontStyle.Bold);
            textBrush = new SolidBrush(Color.FromArgb(255, 200, 200, 200));
            textBackgroudBrush = new SolidBrush(Color.Black);

            Instance.ClientChanged += new EventHandler<ClientChangedEventArgs>(Instance_ClientChanged);
            RegisterClientEvents();
        }

        void MapControl_Disposed(object sender, EventArgs e)
        {
            UnregisterClientEvents(Client);

            downloader.Dispose();

            lock (regionTiles)
            {
                foreach (Image img in regionTiles.Values)
                    if (img != null)
                        img.Dispose();
                regionTiles.Clear();
            }
        }

        void RegisterClientEvents()
        {
            Client.Grid.GridItems += new EventHandler<GridItemsEventArgs>(Grid_GridItems);
            Client.Grid.GridRegion += new EventHandler<GridRegionEventArgs>(Grid_GridRegion);
        }

        void UnregisterClientEvents(GridClient Client)
        {
            if (Client == null) return;
            Client.Grid.GridItems -= new EventHandler<GridItemsEventArgs>(Grid_GridItems);
            Client.Grid.GridRegion -= new EventHandler<GridRegionEventArgs>(Grid_GridRegion);
        }

        Dictionary<ulong, MapItem> regionMapItems = new Dictionary<ulong, MapItem>();
        Dictionary<ulong, GridRegion> regions = new Dictionary<ulong, GridRegion>();

        void Grid_GridItems(object sender, GridItemsEventArgs e)
        {
            foreach (MapItem item in e.Items)
            {
                regionMapItems[item.RegionHandle] = item;
            }

        }

        void Grid_GridRegion(object sender, GridRegionEventArgs e)
        {
            regions[e.Region.RegionHandle] = e.Region;
            if (!UseExternalTiles
                && e.Region.Access != SimAccess.NonExistent
                && e.Region.MapImageID != UUID.Zero
                && !tileRequests.Contains(e.Region.RegionHandle))
                DownloadRegionTile(e.Region.RegionHandle, e.Region.MapImageID);
        }

        void Instance_ClientChanged(object sender, ClientChangedEventArgs e)
        {
            UnregisterClientEvents(e.OldClient);
            RegisterClientEvents();
        }

        public float Zoom
        {
            get { return zoom; }
            set
            {
                if (value >= minZoom && value <= maxZoom)
                {
                    zoom = value;
                    pixelsPerMeter = 1f / zoom;
                    PixRegS = (int)(regionSize / zoom);
                    Invalidate();
                }
            }
        }

        public void ClearTarget()
        {
            targetRegion = nullRegion;
            targetX = targetY = -5000000000d;
            SafeInvalidate();
            ThreadPool.QueueUserWorkItem(sync =>
                {
                    Thread.Sleep(500);
                    SafeInvalidate();
                }
            );
        }

        public void SafeInvalidate()
        {
            if (InvokeRequired)
            {
                if (IsHandleCreated)
                    BeginInvoke(new MethodInvoker(() => Invalidate()));
            }
            else
            {
                if (IsHandleCreated)
                    Invalidate();
            }
        }

        public void CenterMap(ulong regionHandle, uint localX, uint localY)
        {
            uint regionX, regionY;
            Utils.LongToUInts(regionHandle, out regionX, out regionY);
            CenterMap(regionX, regionY, localX, localY);
            Logger.DebugLog(string.Format("{0} {1},{2}", regionHandle, localX, localY));
        }

        public void CenterMap(uint regionX, uint regionY, uint localX, uint localY)
        {
            centerX = (double)regionX * 256 + (double)localX;
            centerY = (double)regionY * 256 + (double)localY;
            centered = true;
            SafeInvalidate();
        }

        public static ulong GlobalPosToRegionHandle(double globalX, double globalY, out float localX, out float localY)
        {
            uint x = ((uint)globalX / 256) * 256;
            uint y = ((uint)globalY / 256) * 256;
            localX = (float)(globalX - (double)x);
            localY = (float)(globalY - (double)y);
            return Utils.UIntsToLong(x, y);
        }

        void Print(Graphics g, float x, float y, string text)
        {
            Print(g, x, y, text, textBrush);
        }

        void Print(Graphics g, float x, float y, string text, Brush brush)
        {
            g.DrawString(text, textFont, textBackgroudBrush, x + 1, y + 1);
            g.DrawString(text, textFont, brush, x, y);
        }

        string GetRegionName(ulong handle)
        {
            if (regions.ContainsKey(handle))
                return regions[handle].Name;
            else
                return string.Empty;
        }

        Dictionary<ulong, Image> regionTiles = new Dictionary<ulong, Image>();
        List<ulong> tileRequests = new List<ulong>();

        void DownloadRegionTile(ulong handle, UUID imageID)
        {
            lock (tileRequests)
                if (!tileRequests.Contains(handle))
                    tileRequests.Add(handle);

            Client.Assets.RequestImage(imageID, (TextureRequestState state, AssetTexture assetTexture) =>
                {
                    switch (state)
                    {
                        case TextureRequestState.Pending:
                        case TextureRequestState.Progress:
                        case TextureRequestState.Started:
                            return;

                        case TextureRequestState.Finished:
                            if (assetTexture != null && assetTexture.AssetData != null)
                            {
                                Image img;
                                OpenMetaverse.Imaging.ManagedImage mi;
                                if (OpenMetaverse.Imaging.OpenJPEG.DecodeToImage(assetTexture.AssetData, out mi, out img))
                                {
                                    regionTiles[handle] = img;
                                    SafeInvalidate();
                                }
                            }
                            goto default;

                        default:
                            lock (tileRequests)
                                if (!tileRequests.Contains(handle))
                                    tileRequests.Add(handle);
                            break;
                    }
                }
            );

            return;

            Uri url = Client.Network.CurrentSim.Caps.CapabilityURI("GetTexture");
            if (url != null)
            {
                downloader.QueueDownlad(
                    new Uri(string.Format("{0}/?texture_id={1}", url.ToString(), imageID.ToString())),
                    null,
                    30 * 1000,
                    null,
                    (HttpWebRequest request, HttpWebResponse response, byte[] responseData, Exception error) =>
                    {
                        Logger.DebugLog(string.Format("{0} - {1}", error, request.RequestUri.ToString()));
                        if (error == null && responseData != null)
                        {
                            try
                            {
                                using (MemoryStream s = new MemoryStream(responseData))
                                {
                                    lock (regionTiles)
                                    {
                                        regionTiles[handle] = Image.FromStream(s);
                                        SafeInvalidate();
                                    }
                                }
                            }
                            catch { }
                            lock (tileRequests)
                                if (tileRequests.Contains(handle))
                                    tileRequests.Remove(handle);

                        }
                    });
            }
        }

        Image GetRegionTile(ulong handle)
        {
            if (regionTiles.ContainsKey(handle))
            {
                return regionTiles[handle];
            }
            return null;
        }

        Image GetRegionTileExternal(ulong handle)
        {
            if (regionTiles.ContainsKey(handle))
            {
                return regionTiles[handle];
            }
            else
            {
                lock (tileRequests)
                {
                    if (tileRequests.Contains(handle)) return null;
                    tileRequests.Add(handle);
                }

                uint regX, regY;
                Utils.LongToUInts(handle, out regX, out regY);
                regX /= regionSize;
                regY /= regionSize;

                downloader.QueueDownlad(
                    new Uri(string.Format("http://map.secondlife.com/map-1-{0}-{1}-objects.jpg", regX, regY)),
                    null,
                    20 * 1000,
                    null,
                    (HttpWebRequest request, HttpWebResponse response, byte[] responseData, Exception error) =>
                    {
                        if (error == null && responseData != null)
                        {
                            try
                            {
                                using (MemoryStream s = new MemoryStream(responseData))
                                {
                                    lock (regionTiles)
                                    {
                                        regionTiles[handle] = Image.FromStream(s);
                                        SafeInvalidate();
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                );

                lock (regionTiles)
                {
                    regionTiles[handle] = null;
                }

                return null;
            }
        }

        void DrawRegion(Graphics g, int x, int y, ulong handle)
        {
            uint regX, regY;
            Utils.LongToUInts(handle, out regX, out regY);
            regX /= regionSize;
            regY /= regionSize;

            string name = GetRegionName(handle);
            Image tile = null;

            if (UseExternalTiles)
                tile = GetRegionTileExternal(handle);
            else
                tile = GetRegionTile(handle);

            if (tile != null)
                g.DrawImage(tile, new Rectangle(x, y - PixRegS, PixRegS, PixRegS));

            if (!string.IsNullOrEmpty(name) && zoom < 3f)
                Print(g, x + 2, y - 16, name);

        }

        List<string> requestedBlocks = new List<string>();

        private void MapControl_Paint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(background);
            if (!centered) return;
            int h = Height, w = Width;

            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;


            float localX, localY;
            ulong centerRegion = GlobalPosToRegionHandle(centerX, centerY, out localX, out localY);
            int pixCenterRegionX = (int)(w / 2 - localX / zoom);
            int pixCenterRegionY = (int)(h / 2 + localY / zoom);

            uint regX, regY;
            Utils.LongToUInts(centerRegion, out regX, out regY);
            regX /= regionSize;
            regY /= regionSize;

            int regLeft = (int)regX - ((int)(pixCenterRegionX / PixRegS) + 1);
            if (regLeft < 0) regLeft = 0;
            int regBottom = (int)regY - ((int)((Height - pixCenterRegionY) / PixRegS) + 1);
            if (regBottom < 0) regBottom = 0;
            int regXMax = 0, regYMax = 0;

            bool foundMyPos = false;
            int myRegX = 0, myRegY = 0;

            for (int ry = regBottom; pixCenterRegionY - (ry - (int)regY) * PixRegS > 0; ry++)
            {
                regYMax = ry;
                for (int rx = regLeft; pixCenterRegionX - ((int)regX - rx) * PixRegS < Width; rx++)
                {
                    regXMax = rx;
                    int pixX = pixCenterRegionX - ((int)regX - rx) * PixRegS;
                    int pixY = pixCenterRegionY - (ry - (int)regY) * PixRegS;
                    ulong handle = Utils.UIntsToLong((uint)rx * regionSize, (uint)ry * regionSize);

                    DrawRegion(g,
                        pixX,
                        pixY,
                        handle);

                    if (Client.Network.CurrentSim.Handle == handle)
                    {
                        foundMyPos = true;
                        myRegX = pixX;
                        myRegY = pixY;
                    }

                }
            }

            float ratio = (float)PixRegS / (float)regionSize;

            if (foundMyPos)
            {
                int myPosX = (int)(myRegX + Client.Self.SimPosition.X * ratio);
                int myPosY = (int)(myRegY - Client.Self.SimPosition.Y * ratio);

                Bitmap icn = Properties.Resources.my_map_pos;
                g.DrawImageUnscaled(icn,
                    myPosX - icn.Width / 2,
                    myPosY - icn.Height / 2
                    );
            }

            int pixTargetX = (int)(Width / 2 + (targetX - centerX) * ratio);
            int pixTargetY = (int)(Height / 2 - (targetY - centerY) * ratio);

            if (pixTargetX >= 0 && pixTargetY < Width &&
                pixTargetY >= 0 && pixTargetY < Height)
            {
                Bitmap icn = Properties.Resources.target_map_pos;
                g.DrawImageUnscaled(icn,
                    pixTargetX - icn.Width / 2,
                    pixTargetY - icn.Height / 2
                    );
                if (!string.IsNullOrEmpty(targetRegion.Name))
                {
                    string label = string.Format("{0} ({1:0}, {2:0})", targetRegion.Name, targetX % regionSize, targetY % regionSize);
                    Print(g, pixTargetX - 8, pixTargetY + 14, label, new SolidBrush(Color.White));
                }
            }

            string block = string.Format("{0},{1},{2},{3}", (ushort)regLeft, (ushort)regBottom, (ushort)regXMax, (ushort)regYMax);
            lock (requestedBlocks)
            {
                if (!requestedBlocks.Contains(block))
                {
                    requestedBlocks.Add(block);
                    Client.Grid.RequestMapBlocks(GridLayerType.Objects, (ushort)regLeft, (ushort)regBottom, (ushort)regXMax, (ushort)regYMax, true);
                }
            }
        }

        #region Mouse handling
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (e.Delta < 0)
                Zoom += 0.25f;
            else
                Zoom -= 0.25f;
            
            if (ZoomChanged != null)
                ZoomChanged(this, EventArgs.Empty);
        }

        bool dragging = false;
        int dragX, dragY, downX, downY;

        private void MapControl_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                dragging = true;
                downX = dragX = e.X;
                downY = dragY = e.Y;
            }
        }

        private void MapControl_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                dragging = false;
                if (e.X == downX && e.Y == downY) // click
                {
                    double ratio = (float)PixRegS / (float)regionSize;
                    targetX = centerX + (double)(e.X - Width / 2) / ratio;
                    targetY = centerY - (double)(e.Y - Height / 2) / ratio;
                    float localX, localY;
                    ulong handle = Helpers.GlobalPosToRegionHandle((float)targetX, (float)targetY, out localX, out localY);
                    uint regX, regY;
                    Utils.LongToUInts(handle, out regX, out regY);
                    if (regions.ContainsKey(handle))
                    {
                        targetRegion = regions[handle];
                        if (MapTargetChanged != null)
                        {
                            MapTargetChanged(this, new MapTargetChangedEventArgs(targetRegion, (int)localX, (int)localY));
                        }
                    }
                    else
                    {
                        targetRegion = new GridRegion();
                    }
                    SafeInvalidate();
                }
            }

        }

        private void MapControl_MouseMove(object sender, MouseEventArgs e)
        {
            if (dragging)
            {
                centerX -= (e.X - dragX) / pixelsPerMeter;
                centerY += (e.Y - dragY) / pixelsPerMeter;
                dragX = e.X;
                dragY = e.Y;
                Invalidate();
            }
        }

        private void MapControl_Resize(object sender, EventArgs e)
        {
            Invalidate();
        }
        #endregion Mouse handling
    }

    public class MapTargetChangedEventArgs : EventArgs
    {
        public GridRegion Region;
        public int LocalX;
        public int LocalY;

        public MapTargetChangedEventArgs(GridRegion region, int x, int y)
        {
            Region = region;
            LocalX = x;
            LocalY = y;
        }
    }

    public class ParallelDownloader : IDisposable
    {
        Thread worker;
        int m_ParallelDownloads = 10;
        bool done = false;
        AutoResetEvent queueHold = new AutoResetEvent(false);
        Queue<QueuedItem> queue = new Queue<QueuedItem>();
        List<HttpWebRequest> activeDownloads = new List<HttpWebRequest>();

        public int ParallelDownloads
        {
            get { return m_ParallelDownloads; }
            set
            {
                m_ParallelDownloads = value;
            }
        }

        public ParallelDownloader()
        {
            worker = new Thread(new ThreadStart(Worker));
            worker.Name = "Parallel Downloader";
            worker.IsBackground = true;
            worker.Start();
        }

        public void Dispose()
        {
            done = true;
            queueHold.Set();
            queue.Clear();

            lock (activeDownloads)
            {
                for (int i = 0; i < activeDownloads.Count; i++)
                {
                    try
                    {
                        activeDownloads[i].Abort();
                    }
                    catch { }
                }
            }

            if (worker.IsAlive)
                worker.Abort();
        }

        private void Worker()
        {
            Logger.DebugLog("Parallel dowloader starting");

            while (!done)
            {
                lock (queue)
                {
                    if (queue.Count > 0)
                    {
                        int nr = 0;
                        lock (activeDownloads) nr = activeDownloads.Count;

                        for (int i = nr; i < ParallelDownloads && queue.Count > 0; i++)
                        {
                            QueuedItem item = queue.Dequeue();
                            HttpWebRequest req = CapsBase.DownloadStringAsync(
                                item.address,
                                item.clientCert,
                                item.millisecondsTimeout,
                                item.downloadProgressCallback,
                                (HttpWebRequest request, HttpWebResponse response, byte[] responseData, Exception error) =>
                                {
                                    lock (activeDownloads) activeDownloads.Remove(request);
                                    item.completedCallback(request, response, responseData, error);
                                    queueHold.Set();
                                }
                            );

                            lock (activeDownloads) activeDownloads.Add(req);
                        }
                    }
                }

                queueHold.WaitOne();
            }

            Logger.DebugLog("Parallel dowloader exiting");
        }

        public void QueueDownlad(Uri address, X509Certificate2 clientCert, int millisecondsTimeout,
            CapsBase.DownloadProgressEventHandler downloadProgressCallback, CapsBase.RequestCompletedEventHandler completedCallback)
        {
            lock (queue)
            {
                queue.Enqueue(new QueuedItem(
                    address,
                    clientCert,
                    millisecondsTimeout,
                    downloadProgressCallback,
                    completedCallback
                    ));
            }
            queueHold.Set();
        }

        public class QueuedItem
        {
            public Uri address;
            public X509Certificate2 clientCert;
            public int millisecondsTimeout;
            public CapsBase.DownloadProgressEventHandler downloadProgressCallback;
            public CapsBase.RequestCompletedEventHandler completedCallback;

            public QueuedItem(Uri address, X509Certificate2 clientCert, int millisecondsTimeout,
            CapsBase.DownloadProgressEventHandler downloadProgressCallback, CapsBase.RequestCompletedEventHandler completedCallback)
            {
                this.address = address;
                this.clientCert = clientCert;
                this.millisecondsTimeout = millisecondsTimeout;
                this.downloadProgressCallback = downloadProgressCallback;
                this.completedCallback = completedCallback;
            }
        }

    }
}
