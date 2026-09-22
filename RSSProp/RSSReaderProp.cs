using System;
using System.IO;
using System.Text;
using Basis;
using com.superneko.bcutils;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SuperNekoya.RSSProp
{
    [Cilboxable]
    public class RSSReaderProp : SimpleVariableSync
    {
        enum State
        {
            Waiting,
            Loading,
            Loaded,
            Error
        }

        public string RSSUrl = string.Empty;
        public int MaxArticleCount = 20;

        // Synced things
        State _state = State.Waiting;
        RssItem[] _rssItems = {};

        // Controls
        public Button ReloadButton;

        // Views
        public GameObject InitialPanel;
        public Button InitialLoadButton;

        public GameObject LoadingPanel;

        public GameObject LoadedPanel;
        public TMP_Text Headlines;

        public GameObject ErrorPanel;
        public TMP_Text ErrorText;

        string _errorString = string.Empty;

        // Others

        BasisNetworkShim _networkShim;

        public override void Start()
        {
            base.Start();

            // Setup callbacks
            InitialLoadButton.onClick.AddListener(Load);
            ReloadButton.onClick.AddListener(Load);

            _networkShim = SafeUtil.MakeNetworkable(this);

            Refresh();
        }

        public void Load()
        {
            _state = State.Loading;
            _rssItems = new RssItem[0];

            SyncToOthers();
            Refresh();

            new BasisStringDownloader().DownloadString(RSSUrl, Loaded);
        }

        public void Loaded(IBasisStringDownload download)
        {
            if (!download.Success)
            {
                _state = State.Error;
                // TODO: Set error message
                return;
            }

            if (string.IsNullOrEmpty(download.Result))
            {
                _state = State.Error;
                // TODO: Set error message
                return;
            }

            try
            {
                _rssItems = RssParser.Parse(download.Result, MaxArticleCount);
            }
            catch (Exception e)
            {
                _state = State.Error;
                _errorString = e.ToString();

                SyncToOthers();
                Refresh();

                return;
            }

            _state = State.Loaded;

            // Re-serialization and distribution
            _networkShim.SendCustomEventDelayedFrames(SyncToOthers, 2);
            _networkShim.SendCustomEventDelayedFrames(Refresh, 4);
        }

        void Refresh()
        {
            Debug.Log("[RSSReader] Refreshed.");

            InitialPanel.SetActive(_state == State.Waiting);
            LoadingPanel.SetActive(_state == State.Loading);
            LoadedPanel.SetActive(_state == State.Loaded);
            ErrorPanel.SetActive(_state == State.Error);

            if (_state == State.Loaded)
            {
                var builder = new StringBuilder();

                foreach (var item in _rssItems)
                {
                    builder.AppendLine(item.Title.Replace("<", "< ").Replace(">", " >"));
                    builder.Append("<size=4>");
                    builder.Append(item.Link.Replace("<", "< ").Replace(">", " >"));
                    builder.AppendLine("</size>");
                }

                Headlines.text = builder.ToString();
            }

            ErrorText.text = _errorString;
        }

        protected override bool DeserializeAndApply(BinaryReader reader)
        {
            try
            {
                _state = (State)reader.ReadInt32();
                var itemLength = reader.ReadInt32();

                var items = new RssItem[itemLength];

                for (var i = 0; i < itemLength; i++)
                {
                    items[i] = new RssItem
                    {
                        Title = reader.ReadString(),
                        Link = reader.ReadString()
                    };
                }

                _rssItems = items;
            }
            catch (Exception e)
            {
                _state = State.Error;
                _errorString = "Failed to receive data from other player:\n" + e.ToString();
                return false;
            }
            finally
            {
                Refresh();
            }

            return true;
        }

        protected override bool Serialize(BinaryWriter writer)
        {
            var items = _rssItems;

            writer.Write((int)_state);
            writer.Write(items.Length);

            foreach (var item in items)
            {
                writer.Write(item.Title);
                writer.Write(item.Link);
            }

            return true;
        }
    }
}
