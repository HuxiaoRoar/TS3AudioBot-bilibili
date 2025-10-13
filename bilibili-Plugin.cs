using Newtonsoft.Json.Linq;
using SixLabors.ImageSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using TS3AudioBot;
using TS3AudioBot.Audio;
using TS3AudioBot.CommandSystem;
using TS3AudioBot.Config;
using TS3AudioBot.Plugins;
using TS3AudioBot.ResourceFactories;

public enum PlayMode
{
    // 顺序播放（播完列表后停止）
    Sequence,
    // 列表循环
    Repeat,
    // 单曲循环
    Single
}


public class BilibiliPlugin : IBotPlugin
{
    private Ts3Client _ts3Client;
    private readonly PlayManager _playManager;
	private static readonly HttpClient http = new HttpClient();
	private static string cookieFile = "bili_cookie.txt";

    // ---> 新增一个字段来存储机器人的默认名称 <---
    private string defaultBotName;

    private static BilibiliVideoInfo lastSearchedVideo;
    private static List<BilibiliVideoInfo> lastHistoryResult;


    // 自建播放列表
    private static readonly List<PlaylistItem> BilibiliPlaylist = new List<PlaylistItem>();
    // 当前播放歌曲在列表中的索引
    private static int currentTrackIndex = -1;
    // 一个标志，用于判断当前是否正在播放由本插件管理的歌曲
    private static bool isPlayingBilibili = false;

    private static PlayMode currentPlayMode = PlayMode.Sequence; // 默认设为顺序播放



    public BilibiliPlugin(PlayManager playManager, Ts3Client ts3Client, TS3AudioBot.Config.ConfBot confBot)
	{
		_playManager = playManager;
        _ts3Client =  ts3Client;

        defaultBotName = confBot.Connect.Name;

        http.DefaultRequestHeaders.Remove("Referer");
		http.DefaultRequestHeaders.Add("Referer", "https://www.bilibili.com");
		http.DefaultRequestHeaders.Remove("User-Agent");
		http.DefaultRequestHeaders.Add(
			"User-Agent",
			"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/138.0.0.0 Safari/537.36 Edg/138.0.0.0"
		);
		LoadCookie();
	}

    public async void Initialize() {
        // 获取当前正在运行的程序集(也就是 BilibiliPlugin.dll)的版本信息
        var version = Assembly.GetExecutingAssembly().GetName().Version;

        // 将版本号格式化成 "主版本.次版本.生成号" 的形式，忽略最后的修订号
        string displayVersion = $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
        await _ts3Client.SendChannelMessage($"Bilibili 插件加载完毕！当前版本：v{displayVersion}");

        // --- 新增代码：订阅播放停止事件 ---
        _playManager.PlaybackStopped += OnPlaybackStopped;
        _playManager.AfterResourceStarted += AfterSongStart;
    }

	public void Dispose() {
        // --- 新增代码：取消订阅，防止内存泄漏 ---
        _playManager.PlaybackStopped -= OnPlaybackStopped;
        _playManager.AfterResourceStarted -= AfterSongStart;
    }

    #region//---------------------------------------------------------------新建类---------------------------------------------------------------//
    // 用于存储单个分P的信息
    public class VideoPartInfo
    {
        public long Cid { get; set; }//分P的CID
        public string Title { get; set; }//分P的标题
        public int Index { get; set; }//分P的索引，从1开始
    }
    // 用于存储整个视频的详细信息
    public class BilibiliVideoInfo
    {
        public string Bvid { get; set; }//bv
        public string Title { get; set; }//标题
        public string Uploader { get; set; }//up主
        public string CoverUrl { get; set; }//封面
        public List<VideoPartInfo> Parts { get; set; } = new List<VideoPartInfo>();
    }
    public class PlaylistItem
    {
        public string Bvid { get; set; }
        public long Cid { get; set; }
        public string Uploader { get; set; }
        public string CoverUrl { get; set; }
        public string Title { get; set; }
        public int PartIndex { get; set; }
        public string PartTitle { get; set; }
        public string Source { get; } = "Bilibili"; // 来源判定标识
        public string RequesterUid { get; set; } // 点歌人的唯一ID
    }
    public class AudioStreamInfo
    {
        public List<string> Urls { get; set; } = new List<string>();
        public bool IsHiRes { get; set; } = false;
    }
    #endregion

    //---------------------------------------------------------------Cookie辅助方法---------------------------------------------------------------//
    
    private void LoadCookie()
	{
		if (File.Exists(cookieFile))
		{
			string cookie = File.ReadAllText(cookieFile);
			if (!string.IsNullOrWhiteSpace(cookie))
			{
				http.DefaultRequestHeaders.Remove("Cookie");
				http.DefaultRequestHeaders.Add("Cookie", cookie);
			}
		}
	}
	
    private string GetCookiePath(InvokerData invoker)
	{
		return $"bili_cookie_{invoker.ClientUid}.txt";
	}
    
    private void SetInvokerCookie(InvokerData invoker, HttpClient client, PlaylistItem track)
    {
        string cookiePath = null;

        if (invoker != null)
        {
            // 情况1：手动触发（点歌、切歌）。直接使用当前操作者的 invoker。
            cookiePath = GetCookiePath(invoker);
        }
        else if (track != null && !string.IsNullOrWhiteSpace(track.RequesterUid))
        {
            // 情况2：自动播放。invoker 为 null，但我们可以从 track 中读取原始点歌人的UID。
            //   Console.WriteLine($"Automatic playback: Using cookie from original requester: {track.RequesterUid}");
            cookiePath = $"bili_cookie_{track.RequesterUid}.txt";
        }

        if (cookiePath != null && File.Exists(cookiePath))
        {
            string cookie = File.ReadAllText(cookiePath);
            if (!string.IsNullOrWhiteSpace(cookie))
            {
                client.DefaultRequestHeaders.Remove("Cookie");
                client.DefaultRequestHeaders.Add("Cookie", cookie);
            }
        }
        // 如果 cookiePath 最终仍为 null（例如非登录用户点的歌，且无全局cookie），则不进行任何操作。
    }
   
    private async Task<string> CheckLoginStatusAsync(string qrKey, InvokerData invoker)
    {
        string checkLoginUrl =
            $"https://passport.bilibili.com/x/passport-login/web/qrcode/poll?qrcode_key={qrKey}";
        string loginStatusResponse;
        bool isLoggedIn = false;
        int time = 0;

        while (isLoggedIn == false)
        {
            loginStatusResponse = await http.GetStringAsync(checkLoginUrl);
            JObject loginStatusJson = JObject.Parse(loginStatusResponse);
            string statusCode = (string)loginStatusJson["data"]?["code"];

            // 打印出登录状态响应
            Console.WriteLine(
                "Login Status Response: " + loginStatusResponse + "statuscode:" + statusCode
            );

            // 登录成功，返回状态码 0
            if (statusCode == "0")
            {
                string fullUrl = (string)loginStatusJson["data"]?["url"];
                isLoggedIn = true;
                string cookie;
                cookie = ExtractCookieFromUrl(fullUrl);
                if (string.IsNullOrWhiteSpace(cookie))
                {
                    return "登录成功，但无法获取Cookie信息。";
                }

                // 保存登录后的cookie信息
                string cookiePath = GetCookiePath(invoker);
                File.WriteAllText(cookiePath, cookie);
                return "扫码登录成功！已将登录信息保存。";
            }

            if (statusCode == "86038")
            {
                return "登录失败，二维码已超时";
            }

            if (time >= 30)
            {
                return "登录失败，超时";
            }

            await Task.Delay(2000); // 每2秒检查一次
            time++;
        }
        return "登录失败，请检查二维码是否已扫描并确认登录。";
    }
    
    private string ExtractCookieFromUrl(string fullUrl)
    {
        // fullUrl 格式：
        // https://passport.biligame.com/crossDomain?DedeUserID=***\u0026DedeUserID__ckMd5=***\u0026Expires=***\u0026SESSDATA=***\u0026bili_jct=***\u0026gourl=https%3A%2F%2Fpassport.bilibili.com

        try
        {
            Uri uri = new Uri(fullUrl);
            var queryParams = System.Web.HttpUtility.ParseQueryString(uri.Query);

            string sessData = queryParams["SESSDATA"];
            string biliJct = queryParams["bili_jct"];

            // 检查是否提取到了这两个参数，并返回 cookie 字符串
            if (!string.IsNullOrEmpty(sessData) && !string.IsNullOrEmpty(biliJct))
            {
                return $"SESSDATA={sessData};bili_jct={biliJct};";
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error extracting cookie from URL: " + ex.Message);
        }

        return null; // 如果解析失败，返回 null
    }

    //---------------------------------------------------------------播放核心方法---------------------------------------------------------------//

    // 播放停止事件的处理
    private async Task OnPlaybackStopped(object sender, EventArgs e)
    {
        if (!isPlayingBilibili || BilibiliPlaylist.Count == 0)
        {
            return;
        }

        // 如果是单曲循环模式，我们需要在播放下一首前，将索引“倒回”一格
        // 因为 PlayNextTrack 方法会固定将索引+1
        if (currentPlayMode == PlayMode.Single)
        {
            currentTrackIndex--;
        }

        await Task.Delay(1000);
        await PlayNextTrack(null);
    }


    // 播放下一首歌的逻辑
    private async Task PlayNextTrack(InvokerData invoker)
    {
        currentTrackIndex++; // 索引正常指向下一首

        if (currentTrackIndex >= BilibiliPlaylist.Count)
        {
            // --- 到达列表末尾时的逻辑 ---
            if (currentPlayMode == PlayMode.Repeat && BilibiliPlaylist.Count > 0)
            {
                // 列表循环模式：回到列表开头
                currentTrackIndex = 0;
            }
            else
            {
                // 顺序播放模式：播放列表已结束
                isPlayingBilibili = false;
                currentTrackIndex = -1;
                BilibiliPlaylist.Clear();
                await _ts3Client.SendChannelMessage("Bilibili 播放列表已结束。");
                // 恢复Bot的默认名称和头像
                await _ts3Client.ChangeName(defaultBotName);
                if (!System.IO.File.Exists("/.dockerenv"))
                {
                    // 如果不是Docker环境，则删除头像
                    await _ts3Client.DeleteAvatar();
                }
                return; // 结束播放流程
            }
        }

        // --- 正常播放 ---
        if (currentTrackIndex < BilibiliPlaylist.Count)
        {
            var nextTrack = BilibiliPlaylist[currentTrackIndex];
            await PlayAudio(nextTrack, invoker);
        }
    }


    // 将歌曲元数据添加到我们的自建播放列表
    private async Task<string> EnqueueAudio(InvokerData invoker, BilibiliVideoInfo videoInfo, VideoPartInfo partInfo, bool announce = true)
    {
        var playlistItem = new PlaylistItem
        {
            Bvid = videoInfo.Bvid,
            Cid = partInfo.Cid,
            Title = videoInfo.Title,
            Uploader = videoInfo.Uploader,
            CoverUrl = videoInfo.CoverUrl,
            PartIndex = partInfo.Index,
            PartTitle = partInfo.Title,
            RequesterUid = invoker?.ClientUid.ToString()


        };

        BilibiliPlaylist.Add(playlistItem);

        if (announce)
        {
            string partTag = (!string.IsNullOrWhiteSpace(partInfo.Title)) ? $"（{partInfo.Index}P：{partInfo.Title}）" : "";
            await _ts3Client.SendChannelMessage($"添加成功！已将《{videoInfo.Title}》{partTag}添加到 Bilibili 播放队列。");
        }
        return null;
    }
   
    // 播放指定歌曲（从 PlaylistItem 获取信息）
    private async Task<string> PlayAudio(PlaylistItem track, InvokerData invoker)
    {
        try
        {
            // 将 PlaylistItem 转换为 GetAudioStreamInfoAsync 需要的格式
            var videoInfo = new BilibiliVideoInfo { Bvid = track.Bvid, Title = track.Title, Uploader = track.Uploader, CoverUrl = track.CoverUrl };
            var partInfo = new VideoPartInfo { Cid = track.Cid, Index = track.PartIndex, Title = track.PartTitle };

            var streamInfo = await GetAudioStreamInfoAsync(videoInfo, partInfo, invoker, track);

            if (streamInfo.Urls == null || streamInfo.Urls.Count == 0)
            {
                // 如果获取链接失败，自动跳到下一首
                await _ts3Client.SendChannelMessage($"《{track.Title}》未能获取到播放链接，已自动跳过。");
                await PlayNextTrack(invoker); // 尝试播放列表的下一首
                return null;
            }

            foreach (var url in streamInfo.Urls)
            {
                if (string.IsNullOrWhiteSpace(url)) continue;
                try
                {
                    string proxyUrl = $"http://localhost:32181/?{WebUtility.UrlEncode(url)}";

                    // 1. 创建 AudioResource 并标记来源
                    var audioResource = new AudioResource(
                        $"https://www.bilibili.com/video/{track.Bvid}", // 使用B站链接作为唯一ID
                        $"{track.Title} - {track.Uploader}",                                  // 歌曲标题
                        "media"                              // 插件专属标识
                    )
                        .Add("PlayUri", track.CoverUrl)
                        .Add("source", "BilibiliPlugin");

                    // 2. 将封面图片下载为 byte[]
                    byte[] coverBytes = null;
                    try
                    {
                        coverBytes = await http.GetByteArrayAsync(track.CoverUrl);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to download cover image: {ex.Message}");
                    }


                    // 3. 使用 MediaPlayResource 进行播放，并补全所有参数
                    await _playManager.Play(invoker, new MediaPlayResource(proxyUrl, audioResource, coverBytes, false));

                    isPlayingBilibili = true; // 关键：标记正在播放B站歌曲




                    // 原有的其他逻辑 (改名、发消息等) 可以保留
                    await SetAvatarAsync(track.CoverUrl);
                    await SetBotNameAsync(track.Title);

                    
                    Console.WriteLine($"{track.Title}播放成功：{proxyUrl}");
                    string qualityTag = streamInfo.IsHiRes ? " (Hi-Res)" : "";
                    string partTag = (!string.IsNullOrWhiteSpace(track.PartTitle)) ? $"（{track.PartIndex}P：{track.PartTitle}）" : "";
                    string partJump = (!string.IsNullOrWhiteSpace(track.PartTitle)) ? $"/?p={track.PartIndex}" : "";
                    await _ts3Client.SendChannelMessage($"正在播放{qualityTag}：{track.Uploader} 投稿的《{track.Title}》{partTag}{System.Environment.NewLine}链接：https://www.bilibili.com/video/{track.Bvid}{partJump}");
                    return null; // 播放成功，返回null
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"播放失败：{url}\n原因: {ex.Message}");
                }
            }
            // 如果所有链接都尝试失败
            await _ts3Client.SendChannelMessage($"《{track.Title}》所有音频链接均播放失败，已自动跳过。");
            await PlayNextTrack(invoker); // 尝试播放列表的下一首
            return null;
        }
        catch (Exception ex)
        {
            return "播放失败：" + ex.Message;
        }
    }

    //---------------------------------------------------------------虎啸添加---------------------------------------------------------------//
    private async Task<BilibiliVideoInfo> GetVideoInfo(string bvid)
    {
        try
        {
            string viewApi = $"https://api.bilibili.com/x/web-interface/view?bvid={bvid}";
            string viewJson = await http.GetStringAsync(viewApi);
            JObject viewData = JObject.Parse(viewJson)["data"] as JObject;

            if (viewData == null)
            {
                // 如果无法获取视频数据，可以抛出异常或返回 null
                throw new Exception("未获取到视频信息，请检查 BV 号是否正确。");
            }

            var videoInfo = new BilibiliVideoInfo
            {
                Bvid = viewData["bvid"]?.ToString(),
                Title = viewData["title"]?.ToString(),
                Uploader = viewData["owner"]?["name"]?.ToString(),
                CoverUrl = viewData["pic"]?.ToString()
            };

            videoInfo.CoverUrl = await GetFormattedCoverUrlAsync(videoInfo.CoverUrl);

            JArray pages = viewData["pages"] as JArray;
            if (pages != null && pages.Count > 1)
            {
                // 多分P视频
                for (int i = 0; i < pages.Count; i++)
                {
                    videoInfo.Parts.Add(new VideoPartInfo
                    {
                        Cid = (long)pages[i]["cid"],
                        Title = pages[i]["part"]?.ToString(),
                        Index = i + 1
                    });
                }
            }
            else
            {
                // 单P视频
                videoInfo.Parts.Add(new VideoPartInfo
                {
                    Cid = (long)viewData["cid"],
                    Title = "", // 单P视频没有分P标题，直接使用主标题
                    Index = 1 
                });
            }

            return videoInfo;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"获取视频信息时出错 (bvid: {bvid}): {ex.Message}");
            // 向上抛出异常，让调用方处理
            throw;
        }
    }

    private async Task<string> SetAvatarAsync(string imageUrl)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            return "图片URL为空，无法设置头像。";
        }
               
        try
        {
            await MainCommands.CommandBotAvatarSet(_ts3Client, imageUrl);
            return null;
        }
        catch (Exception ex)
        {
            return $"错误：修改头像失败。原因: {ex.Message}。请检查是否给机器人赋权。";
        }
    }

    private async Task<string> SetBotNameAsync(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "标题为空，无法设置机器人名称。";
        }

        try
        {
            // 1. 检查标题长度，如果超过30个字符，则截断为27个字符并加上"..."
            string botName = title.Length > 30 ? title.Substring(0, 27) + "..." : title;

            // 2. 调用客户端API修改名称 
             await _ts3Client.ChangeName(botName);
            // 3. 成功后，返回 null
            return null;
        }
        catch (Exception ex)
        {
            // 4. 失败时，返回错误信息
            return $"错误：修改机器人名称失败。原因: {ex.Message}。请检查是否给机器人赋权。";
        }
    }

    private async Task<AudioStreamInfo> GetAudioStreamInfoAsync(BilibiliVideoInfo videoInfo, VideoPartInfo partInfo, InvokerData invoker, PlaylistItem track)
    {
        //if (invoker == null)
        //{
        //    Console.WriteLine("GetAudioStreamInfoAsync: invoker is NULL. This is likely an automatic track change. Using default cookie.");
        //}
        //else
        //{
        //    Console.WriteLine($"GetAudioStreamInfoAsync: invoker is  ({invoker.ClientUid}). Using user-specific cookie.");
        //}
        var streamInfo = new AudioStreamInfo();

        try
        {
            // 1. 创建一个临时的、携带用户个人Cookie的HttpClient
            var userClient = new HttpClient();
            userClient.DefaultRequestHeaders.Add("Referer", "https://www.bilibili.com");
            userClient.DefaultRequestHeaders.Add(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/138.0.0.0 Safari/537.36 Edg/128.0.0.0"
            );
            // 调用我们现有的 SetInvokerCookie 方法来设置当前用户的 Cookie
            SetInvokerCookie(invoker, userClient, track);

            // 2. 使用这个临时客户端请求播放链接API
            string playApi = $"https://api.bilibili.com/x/player/playurl?cid={partInfo.Cid}&bvid={videoInfo.Bvid}&fnval=16&fourk=1";
            string playJson = await userClient.GetStringAsync(playApi);
            JObject playData = JObject.Parse(playJson);
            // Console.WriteLine("-------------------------------------------------------------");
            // Console.WriteLine(playApi);
            // Console.WriteLine(playData.ToString(Newtonsoft.Json.Formatting.Indented));
            // Console.WriteLine("-------------------------------------------------------------");


            // 3. 优先尝试获取 Hi-Res (flac) 音频            
            var flacAudio = playData.SelectToken("data.dash.flac.audio") as JObject;
            if (flacAudio != null && flacAudio.HasValues)
            {
                Console.WriteLine("发现 Hi-Res 音频流，优先尝试...");
                streamInfo.IsHiRes = true;
                // 提取所有可能的URL
                if (flacAudio["baseUrl"] != null) streamInfo.Urls.Add(flacAudio["baseUrl"].ToString());
                if (flacAudio["backupUrl"] is JArray backupUrls) streamInfo.Urls.AddRange(backupUrls.Select(u => u.ToString()));
            }

            // 4. 如果没有Hi-Res，则回退到获取普通的DASH音频
            if (!streamInfo.IsHiRes == true) Console.WriteLine("正在获取标准 DASH 音频...");
                JArray audioArray = playData["data"]?["dash"]?["audio"] as JArray;

            //       Console.WriteLine(audioArray.ToString(Newtonsoft.Json.Formatting.Indented));

            if (audioArray != null)
                {
                    // 选择码率最高的音轨
                    JObject bestAudio = audioArray.OrderByDescending(a => (long)a["bandwidth"]).FirstOrDefault() as JObject;
                    if (bestAudio != null)
                    {
                        // 提取所有可能的URL
                        if (bestAudio["baseUrl"] != null) streamInfo.Urls.Add(bestAudio["baseUrl"].ToString());
                        if (bestAudio["backupUrl"] is JArray backupUrls) streamInfo.Urls.AddRange(backupUrls.Select(u => u.ToString()));
                    }
                }
            
            return streamInfo;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"获取音频流信息失败: {ex.Message}");
            // 即使失败，也返回一个空的 streamInfo 对象，避免后续代码出错
            return streamInfo;
        }
    }
    
    private async Task<string> HandleVideoRequest(InvokerData invoker, string input,

        Func<InvokerData, BilibiliVideoInfo, VideoPartInfo, Task<string>> actionAsync

    )
    {
        string bvid = input;
        int requestedPartIndex = -1;

        if (input.Contains("-"))
        {
            var parts = input.Split('-');
            if (parts.Length == 2 && int.TryParse(parts[1], out int pIndex))
            {
                bvid = parts[0];
                requestedPartIndex = pIndex;
            }
        }

        try
        {
            var videoInfo = await GetVideoInfo(bvid);
            lastSearchedVideo = videoInfo;

            if (requestedPartIndex > 0)
            {
                var partToProcess = videoInfo.Parts.FirstOrDefault(p => p.Index == requestedPartIndex);
                if (partToProcess != null)
                {
                    // 注意：这里调用 actionAsync 时不再传入 invoker
                    return await actionAsync(invoker, videoInfo, partToProcess);
                }
                else
                {
                    if (videoInfo.Parts.Count == 1)
                    {
                        string response = $"该视频只有一个分P，即将为您处理。\n\n";
                        // 注意：这里调用 actionAsync 时不再传入 invoker
                        response += await actionAsync(invoker, videoInfo, videoInfo.Parts.First());
                        return response;
                    }
                    else
                    {
                        string reply = $"分P选择错误！您输入的 ‘{requestedPartIndex}’ 不在有效范围内 (1 - {videoInfo.Parts.Count})。\n";
                        reply += $"视频《{videoInfo.Title}》包含 {videoInfo.Parts.Count} 个分P：\n";
                        foreach (var part in videoInfo.Parts)
                        {
                            reply += $"{part.Index}. {part.Title}\n";
                        }
                        reply += $"\n请使用命令 !b vp [编号] 或 !b addp [编号] 重新选择。";
                        return reply;
                    }
                }
            }
            else
            {
                if (videoInfo.Parts.Count > 1)
                {
                    string reply = $"视频《{videoInfo.Title}》包含 {videoInfo.Parts.Count} 个分P：\n";
                    foreach (var part in videoInfo.Parts)
                    {
                        reply += $"{part.Index}. {part.Title}\n";
                    }
                    reply += "\n请使用命令 !b vp [编号] 播放对应分P，或使用 !b addp [编号] 添加到队列。";
                    return reply;
                }
                else
                {
                    // 注意：这里调用 actionAsync 时不再传入 invoker
                    return await actionAsync(invoker,videoInfo, videoInfo.Parts.First());
                }
            }
        }
        catch (Exception ex)
        {
            return "处理视频请求时出错：" + ex.Message;
        }
    }
   
    private List<BilibiliVideoInfo> ParsePagesToList(JObject viewData)
    {
        var videoList = new List<BilibiliVideoInfo>();
        var baseTitle = viewData["title"]?.ToString();
        var uploader = viewData["owner"]?["name"]?.ToString();
        var coverUrl = viewData["pic"]?.ToString();
        var bvid = viewData["bvid"]?.ToString();

        JArray pages = viewData["pages"] as JArray;
        if (pages == null) return videoList;

        foreach (var page in pages)
        {
            var videoInfo = new BilibiliVideoInfo
            {
                Bvid = bvid,
                Title = baseTitle,
                Uploader = uploader,
                CoverUrl = coverUrl,
                Parts = new List<VideoPartInfo>
                {
                    new VideoPartInfo
                    {
                        Cid = (long)page["cid"],
                        Title = page["part"]?.ToString(),
                        Index = (int)page["page"]
                    }
                }
            };
            videoList.Add(videoInfo);
        }
        return videoList;
    }
    
    private List<BilibiliVideoInfo> ParseSeasonToList(JObject viewData)
    {
        var videoList = new List<BilibiliVideoInfo>();
        var uploader = viewData["owner"]?["name"]?.ToString(); // 共用合集创建者的名字

        var episodes = viewData.SelectTokens("ugc_season.sections[*].episodes[*]").ToList();

        foreach (var episode in episodes)
        {
            var episodeBvid = episode["bvid"]?.ToString();
            var episodeTitle = episode["title"]?.ToString();
            var episodeCover = episode["arc"]?["pic"]?.ToString();

            // 关键：将合集内视频的内嵌分P也“展平”
            if (episode["pages"] is JArray pages && pages.Count > 1)
            {
                foreach (var page in pages)
                {
                    var videoInfo = new BilibiliVideoInfo
                    {
                        Bvid = episodeBvid,
                        Title = episodeTitle,
                        Uploader = uploader,
                        CoverUrl = episodeCover,
                        Parts = new List<VideoPartInfo>
                        {
                            new VideoPartInfo
                            {
                                Cid = (long)page["cid"],
                                Title = page["part"]?.ToString(),
                                Index = (int)page["page"]
                            }
                        }
                    };
                    videoList.Add(videoInfo);
                }
            }
            else
            {
                // 对于合集内部的单P视频，只添加一项
                var videoInfo = new BilibiliVideoInfo
                {
                    Bvid = episodeBvid,
                    Title = episodeTitle,
                    Uploader = uploader,
                    CoverUrl = episodeCover,
                    Parts = new List<VideoPartInfo>
                        {
                            new VideoPartInfo
                            {
                                Cid = (long)episode["cid"], // 从episode层级获取cid
                                Title = "", // 单P没有分P标题
                                Index = 1
                            }
                        }
                };
                videoList.Add(videoInfo);
            }
        }   
   
        return videoList;
    } 
    
    private async Task<string> BatchAddAsync(InvokerData invoker, List<BilibiliVideoInfo> videoList, string summaryMessage)
    {
        if (videoList == null || videoList.Count == 0)
            return "要添加的列表为空。";

        int successCount = 0;
        foreach (var video in videoList)
        {
            try
            {
                var part = video.Parts.First();
                // 调用新的 EnqueueAudio，只是不发送单条通知
                var result = await EnqueueAudio(invoker, video, part, announce: false);
                if (result == null)
                {
                    successCount++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"批量添加时发生错误 (BVID: {video.Bvid}): {ex.Message}");
            }
        }

        if (successCount > 0)
        {
            // 发送总结信息
            await _ts3Client.SendChannelMessage(summaryMessage);
            return null;
        }
        else
        {
            return "批量添加失败，未能成功添加任何歌曲。";
        }
    }
    // 旋转添加的核心逻辑，现在统一了播放和添加合集的操作   
    private async Task<string> BatchAddRotatedAsync(InvokerData invoker, List<BilibiliVideoInfo> fullList, string targetBvid, string collectionTitle, bool startPlaying)
    {
        int startIndex = fullList.FindIndex(v => v.Bvid == targetBvid);
        if (startIndex == -1) startIndex = 0; // 如果找不到，就从第一个开始

        // 创建旋转后的列表，确保 targetBvid 是第一个
        var rotatedList = fullList.Skip(startIndex).Concat(fullList.Take(startIndex)).ToList();

        string summaryMessage = $"已将合集《{collectionTitle}》中的 {rotatedList.Count} 首歌曲加入队列。";
        await BatchAddAsync(invoker, rotatedList, summaryMessage);

        // 如果指令要求立即播放，并且当前没有在播放B站的歌
        if (startPlaying && !isPlayingBilibili)
        {
            // 计算新添加的歌曲在我们总列表中的起始索引，然后-1，因为 PlayNextTrack 会先+1
            currentTrackIndex = BilibiliPlaylist.Count - rotatedList.Count - 1;
            await PlayNextTrack(invoker);
        }
        return null;
    }

    // 检查音频来源
    private Task AfterSongStart(object sender, PlayInfoEventArgs e)
    {
        // 检查音频来源，如果来源不是本插件，则释放播放控制权
        if (e.ResourceData?.Get("source") != "BilibiliPlugin")
        {
            isPlayingBilibili = false;            
        }
        return Task.CompletedTask;
    }

    // 格式图片URL
    private async Task<string> GetFormattedCoverUrlAsync(string imageUrl)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            return imageUrl; // 如果URL为空，直接返回
        }

        int size = 500; // 默认尺寸

        try
        {
            // 1. 不下载图片，仅获取图片信息流
            using (var response = await http.GetAsync(imageUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                using (var stream = await response.Content.ReadAsStreamAsync())
                {
                    // 2. 使用 SixLabors.ImageSharp.Image.IdentifyAsync 获取尺寸
                    var imageInfo = await Image.IdentifyAsync(stream);
                    if (imageInfo != null)
                    {
                        // 3. 二者取最小值
                        size = Math.Min(imageInfo.Width, imageInfo.Height);
                    }
                    else
                    {
                        Console.WriteLine("警告：获取图片宽高失败，将使用默认尺寸 500。");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"警告：获取图片信息失败，将使用默认尺寸 500。原因: {ex.Message}");
            // 发生任何异常（如网络请求失败），都继续使用默认尺寸
        }

        // 4. 拼接B站特定格式的图片处理URL
        // B站的格式是 @<高度>h_<宽度>w_1c，我们要做成正方形，所以宽高用同一个最小值
        return $"{imageUrl}@{size}h_{size}w_1c";
    }


    #region    //---------------------------------------------------------------列表方法---------------------------------------------------------------//
    // 格式化单行文本，确保对齐
    private static string FormatLine(string index, string title, string uploader, int indexWidth, int titleWidth, int uploaderWidth)
    {
        return $"{PadText(index, indexWidth)} | {PadText(title, titleWidth)} | {PadText(uploader, uploaderWidth)}";
    }

    // 使用了精确宽度计算并直接舍弃小数的 PadText 方法

    private static string PadText(string input, int targetWidth)
    {
        if (string.IsNullOrEmpty(input))
            return new string(' ', targetWidth);

        double currentWidth = 0;
        var resultBuilder = new System.Text.StringBuilder();

        // 1. 初步构建字符串，看是否需要截断
        foreach (char c in input)
        {
            currentWidth += GetCharWidth(c);
            resultBuilder.Append(c);
        }

        // 2. 如果初步构建的总宽度不大于目标宽度，直接填充空格并返回
        if (currentWidth <= targetWidth)
        {
            // 直接舍弃小数部分来填充
            int truncatedWidth = (int)currentWidth;
            while (truncatedWidth < targetWidth)
            {
                resultBuilder.Append(' ');
                truncatedWidth++;
            }
            return resultBuilder.ToString();
        }

        // 3. 如果需要截断，则执行带省略号的逻辑
        else
        {
            resultBuilder.Clear();
            currentWidth = 0;
            

            // 重新构建字符串，但这次要为省略号预留空间
            foreach (char c in input)
            {
                double charWidth = GetCharWidth(c);
                // 关键逻辑：如果加上当前字符和省略号就会超出，则停止
                if (currentWidth + charWidth + 3 > targetWidth)
                {
                    break;
                }
                resultBuilder.Append(c);
                currentWidth += charWidth;
            }

            resultBuilder.Append("...");
            currentWidth += 3;

            // 填充剩余的空格
            int truncatedWidth = (int)currentWidth;
            while (truncatedWidth < targetWidth)
            {
                resultBuilder.Append(' ');
                truncatedWidth++;
            }
            return resultBuilder.ToString();
        }
    }

    // 核心：精确计算每个字符显示宽度的方法
    private static double GetCharWidth(char c)
    {
        if (IsChinese(c))
        {
            return 4; // 中文等价4个空格
        }
        // 处理西文字符和数字
        switch (c)
        {
            // 5档 (最宽)
            case 'M':
            case 'W':
            case 'm':
            case '《':
            case '》':
            case '【':
            case '】':
                return 4;

            // 4档 (较宽)
            case '@':
            case 'Q':
            case 'O':
            case 'G':
            case 'D':
            case 'H':
            case 'V':
            case 'U':
            case 'N':
                return 3;

            // 3档 (中等)
            case 'b':
            case 'd':
            case 'g':
            case 'h':
            case 'n':
            case 'o':
            case 'p':
            case 'q':
            case 'u':
            case 'A':
            case 'B':
            case 'C':
            case 'K':
            case 'P':
            case 'R':
            case 'X':
            case 'Y':
                return 2.5;

            // 1档 (最窄)
            case 'i':
            case 'j':
            case 'l':
            case 't':
            case 'f':
            case '.':
            case ',':
            case ';':
            case ':':
            case '!':
                return 1;
        }

        // 2档 (较窄)
        if (char.IsUpper(c) || char.IsLower(c) || char.IsDigit(c) || c == '*'|| c == '：')
        {
            return 2; // 其余大小写 字母 数字 等价2个
        }

        // 默认其他所有字符（如多数符号、空格等）为1个单位宽度
        return 1;
    }

    // 判断字符是否为中文的方法
    private static bool IsChinese(char c)
    {
        // 这个范围涵盖了中日韩统一表意文字等主要东亚字符
        if ((c >= 0x3040 && c <= 0x309F) || // 日文平假名
             (c >= 0x30A0 && c <= 0x30FF) || // 日文片假名，韩文字母
             (c >= 0x4E00 && c <= 0x9FFF) || // CJK统一表意文字
             (c >= 0xAC00 && c <= 0xD7AF))   // 韩文音节
        {
            return true;
        }
        return false;
    }

    private async Task ShowPlaylistAsync(int page = 1)
    {
        if (BilibiliPlaylist.Count == 0)
        {
            await _ts3Client.SendChannelMessage("Bilibili 播放列表是空的。");
            return;
        }

        const int itemsPerPage = 15; // 每页显示15项
        int totalPages = (BilibiliPlaylist.Count + itemsPerPage - 1) / itemsPerPage;

        if (page < 1 || page > totalPages)
        {
            await _ts3Client.SendChannelMessage($"页码无效，请输入 1 到 {totalPages} 之间的页码。");
            return;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Bilibili 播放列表:");
        sb.AppendLine(FormatLine("索引", "歌曲标题", "UP主", 10, 72, 35));
        sb.AppendLine("-----------------------------------------------------------------------------------------------------");

        int startIndex = (page - 1) * itemsPerPage;
        for (int i = startIndex; i < startIndex + itemsPerPage && i < BilibiliPlaylist.Count; i++)
        {
            var item = BilibiliPlaylist[i];

            // 标记当前播放的歌曲
            string index_string = (i == currentTrackIndex) ? $" {i + 1}*" : (i + 1).ToString();


            // 组合主标题和分P标题
            string displayTitle = item.Title;
            if (!string.IsNullOrWhiteSpace(item.PartTitle))
            {
                displayTitle = $" (P{item.PartIndex}: {item.PartTitle})" + displayTitle;
            }

            sb.AppendLine(FormatLine(index_string, displayTitle, item.Uploader, 10, 70, 35));
        }

        sb.AppendLine($"--- 第 {page} / {totalPages} 页 | 共 {BilibiliPlaylist.Count} 首歌曲 ---");

        await _ts3Client.SendChannelMessage(sb.ToString());
    }

    #endregion

    #region    //---------------------------------------------------------------代码部分---------------------------------------------------------------//

    [Command("b status")]
    public async Task<string> BilibiliStatus(InvokerData invoker)
    {
        // 准备一个临时的 HttpClient 来发送请求
        var client = new HttpClient();
        client.DefaultRequestHeaders.Add("Referer", "https://www.bilibili.com");
        client.DefaultRequestHeaders.Add(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/138.0.0.0 Safari/537.36 Edg/128.0.0.0"
        );

        SetInvokerCookie(invoker, client, null);
        Console.WriteLine($"此登录用户为“invoker.ClientUid”");
        string proxyStatus;
        try
        {
            // 尝试快速连接代理来检查其状态
            using (var proxyClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) })
            {
                var response = await proxyClient.GetAsync("http://localhost:32181/");
                proxyStatus = "http://localhost:32181/ (运行中)";
            }
        }

        catch (Exception)
        {
            proxyStatus = "未启动代理，建议启动！";
        }

        try
        {
            // 访问 Bilibili API
            string navJson = await client.GetStringAsync("https://api.bilibili.com/x/web-interface/nav");
            JObject navData = JObject.Parse(navJson);

            var reply = new System.Text.StringBuilder();
            reply.AppendLine("B站登录状态");
            reply.AppendLine();
            reply.AppendLine($"代理接口：{proxyStatus}");
            bool isLogin = false;
            
            JObject data = null;

            if (navData["code"]?.ToString() == "0" && navData["data"] != null)
            {
                data = navData["data"] as JObject;
                isLogin = (bool?)data?["isLogin"] ?? false;
            

            }
            if (isLogin)
            {
                string uname = data["uname"]?.ToString() ?? "未知用户";
                string mid = data["mid"]?.ToString() ?? "N/A";
                string vipLabel = data["vip_label"]?["text"]?.ToString();
                long vipDueDateUnix = (long?)data["vipDueDate"] ?? 0;

                reply.Append($"当前用户：{uname} [https://space.bilibili.com/{mid}]");

                if (!string.IsNullOrEmpty(vipLabel))
                {
                    reply.AppendLine(); // 换行
                    string dueDateString = "N/A";
                    if (vipDueDateUnix > 0)
                    {
                        // 从Unix毫秒时间戳转换为正常日期
                        DateTimeOffset dueDate = DateTimeOffset.FromUnixTimeMilliseconds(vipDueDateUnix);
                        dueDateString = dueDate.ToString("yyyy-MM-dd");
                    }
                    reply.Append($"会员状态：{vipLabel} (到期：{dueDateString})");
                }
            }
            else
            {
                reply.Append("当前用户：未登录");
            }

            return reply.ToString();
        }
        catch (Exception ex)
        {
            return $"检查状态时发生错误：{ex.Message}";
        }
    }

    [Command("b qr")]
	public async Task<string> BilibiliQrLogin(InvokerData invoker)
	{
		try
		{
			// 1. 请求生成二维码的key
			string keyUrl = "https://passport.bilibili.com/x/passport-login/web/qrcode/generate";
			string keyResponse = await http.GetStringAsync(keyUrl);
			JObject keyJson = JObject.Parse(keyResponse);

			string qrKey = keyJson["data"]?["qrcode_key"]?.ToString();
			string qrUrl = keyJson["data"]?["url"]?.ToString();

			Console.WriteLine(
				"Key Response: " + keyResponse + "\n qrUrl:" + qrUrl + "\n qrKey:" + qrKey
			);

			if (string.IsNullOrEmpty(qrUrl))
			{
				return "获取二维码失败，请稍后再试，返回的Url为空。";
			}

			qrUrl = qrUrl.Replace(@"\u0026", "&"); // 解决\u0026替换问题

			// 生成二维码
			var qrCodeUrl =
				$"[URL]https://api.2dcode.biz/v1/create-qr-code?data={Uri.EscapeDataString(qrUrl)}[/URL]";

			//轮询
			_ = CheckLoginStatusAsync(qrKey, invoker);
			// 3. 返回二维码图片的URL或其他方式将二维码显示给用户
			// 如果是发送二维码图片到 TS3AudioBot，可以将 qrCodeUrl 提供给用户
			return $"请扫描二维码进行登录： {qrCodeUrl}";
		}
		catch (Exception ex)
		{
			return $"二维码登录失败：{ex.Message}";
		}
	}

	[Command("b login")]
	public async Task<string> BilibiliLogin(InvokerData invoker, string cookie)
	{
		if (string.IsNullOrWhiteSpace(cookie))
			return "用法: !b login [SESSDATA=xxx; bili_jct=xxx; ...]";

		string cookiePath = GetCookiePath(invoker);
		File.WriteAllText(cookiePath, cookie);

		try
		{
			// 使用新的 HttpClient（避免 Header 污染）
			var client = new HttpClient();
			client.DefaultRequestHeaders.Add("Cookie", cookie);

			string userJson = await client.GetStringAsync(
				"https://api.bilibili.com/x/web-interface/nav"
			);
			JObject userObj = JObject.Parse(userJson);
			string uname = userObj["data"]?["uname"]?.ToString();

			if (!string.IsNullOrEmpty(uname))
				return $"登录成功，账号绑定为：{uname}";

			return "Cookie 已设置，但未能确认登录状态，请检查 Cookie 是否有效。";
		}
		catch (Exception ex)
		{
			return "登录状态确认失败：" + ex.Message;
		}
	}   

    [Command("b history")]
	public async Task<string> BilibiliHistory(InvokerData invoker)
	{
		try
		{
			// 新建HttpClient，避免全局Cookie污染
			var client = new HttpClient();
			string cookiePath = GetCookiePath(invoker);
			if (File.Exists(cookiePath))
			{
				string cookie = File.ReadAllText(cookiePath);
				if (!string.IsNullOrWhiteSpace(cookie))
				{
					client.DefaultRequestHeaders.Add("Cookie", cookie);
				}
			}

            string url = "https://api.bilibili.com/x/web-interface/history/cursor?ps=10&type=archive";
			string json = await client.GetStringAsync(url);
			JObject data = JObject.Parse(json)["data"] as JObject;
			JArray list = data?["list"] as JArray;

			if (list == null || list.Count == 0)
				return "未获取到历史记录，请确认当前用户是否登录账号。";

            lastHistoryResult = new List<BilibiliVideoInfo>();
            string reply = "最近观看的视频：\n";

			for (int i = 0; i < list.Count; i++)
			{
				JObject item = (JObject)list[i];
				JObject history = item["history"] as JObject;

                // 提取分P标题，优先使用 show_title
                string partTitle = item["show_title"]?.ToString();

                long pageNumber = (long?)history?["page"] ?? 0;

                // 创建并填充视频信息对象
                var videoInfo = new BilibiliVideoInfo
                {
                    Bvid = history?["bvid"]?.ToString(),
                    Title = item["title"]?.ToString(),
                    Uploader = item["author_name"]?.ToString(),
                    CoverUrl = item["cover"]?.ToString(),
                    // 历史记录只关心播放的那一个P

                    Parts = new List<VideoPartInfo>
                {
                    new VideoPartInfo
                    {
                        Cid = (long?)history?["cid"] ?? 0,
                        Title = partTitle,
                        Index = (int)pageNumber
                    }
                }
                };

                // 检查关键信息是否存在
                if (!string.IsNullOrWhiteSpace(videoInfo.Bvid) && videoInfo.Parts.First().Cid > 0)
                {
                    lastHistoryResult.Add(videoInfo);

                    // 根据您的要求格式化输出
                    string displayTitle = videoInfo.Title;
                    // 如果分P标题和主标题不同，则拼接显示
                    if (!string.IsNullOrWhiteSpace(partTitle) )
                    {
                        displayTitle += $"({pageNumber}P：{partTitle})";
                    }
                    reply += $"{lastHistoryResult.Count}. {displayTitle}\n";
                }
            }
            reply += "\n使用 !b h [编号] 播放对应视频。\n使用 !b addh [编号] 添加到下一播放。";
            return reply;
            
        }
        catch (Exception ex)
		{
			return "获取历史记录失败：" + ex.Message;
		}
	}

    [Command("b h")]
    public async Task<string> BilibiliHistoryPlay(InvokerData invoker, int index)
    {
        if (lastHistoryResult == null || index < 1 || index > lastHistoryResult.Count)
            return "请输入有效编号。";

        var videoToPlay = lastHistoryResult[index - 1];
        var partToPlay = videoToPlay.Parts.First();

        // 创建要插入的播放项
        var playlistItem = new PlaylistItem
        {
            Bvid = videoToPlay.Bvid,
            Cid = partToPlay.Cid,
            Title = videoToPlay.Title,
            Uploader = videoToPlay.Uploader,
            CoverUrl = videoToPlay.CoverUrl,
            PartIndex = partToPlay.Index,
            PartTitle = partToPlay.Title,
            RequesterUid = invoker?.ClientUid.ToString()
        };

        // 计算插入位置（当前播放歌曲的下一首）
        int insertIndex = (currentTrackIndex < 0) ? BilibiliPlaylist.Count : currentTrackIndex + 1;
        BilibiliPlaylist.Insert(insertIndex, playlistItem);

        // 将播放索引指向新插入歌曲的前一首，然后调用 PlayNextTrack 立即播放
        currentTrackIndex = insertIndex - 1;
        await PlayNextTrack(invoker);

        return null;
    }

    [Command("b addh")]
    public async Task<string> BilibiliHistoryAdd(InvokerData invoker, int index)
    {
        if (lastHistoryResult == null || lastHistoryResult.Count == 0)
            return "历史记录为空，请先使用 !b history 加载最近观看的视频。";

        if (index < 1 || index > lastHistoryResult.Count)
            return $"请输入有效编号（1 - {lastHistoryResult.Count}）。";

        var videoToAdd = lastHistoryResult[index - 1];
        var partToAdd = videoToAdd.Parts.First();

        // 直接调用新的 EnqueueAudio，它只会添加到列表
        return await EnqueueAudio(invoker, videoToAdd, partToAdd);
    }

    [Command("b v")]
    public async Task<string> BilibiliBvCommand(InvokerData invoker, string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "请提供 BV 号。";

        // --- 播放全部分P的情况 (-a) ---
        if (input.EndsWith("-a", StringComparison.OrdinalIgnoreCase))
        {
            string bvid = input.Substring(0, input.Length - 2);
            try
            {
                string viewJson = await http.GetStringAsync($"https://api.bilibili.com/x/web-interface/view?bvid={bvid}");
                JObject viewData = JObject.Parse(viewJson)["data"] as JObject;
                var videoList = ParsePagesToList(viewData);
                if (videoList.Count <= 1) return "该视频没有多个分P。";

                // 将解析出的视频列表转换为 PlaylistItem 列表
                var itemsToInsert = videoList.Select(video =>
                {
                    var part = video.Parts.First();
                    return new PlaylistItem
                    {
                        Bvid = video.Bvid,
                        Cid = part.Cid,
                        Title = video.Title,
                        Uploader = video.Uploader,
                        CoverUrl = video.CoverUrl,
                        PartIndex = part.Index,
                        PartTitle = part.Title,
                        RequesterUid = invoker?.ClientUid.ToString()
                    };
                }).ToList();

                // 计算插入位置（当前播放歌曲的下一首）
                int insertIndex = (currentTrackIndex < 0) ? BilibiliPlaylist.Count : currentTrackIndex + 1;
                BilibiliPlaylist.InsertRange(insertIndex, itemsToInsert);

                await _ts3Client.SendChannelMessage($"已将《{viewData["title"]}》的 {videoList.Count} 个分P插入队列并立即播放。");

                // 将播放索引指向新插入歌曲的前一首，然后调用 PlayNextTrack
                currentTrackIndex = insertIndex - 1;
                await PlayNextTrack(invoker);
                return null;
            }
            catch (Exception ex) { return $"播放全部分P失败: {ex.Message}"; }
        }
        // --- 播放单个或指定P的情况 ---
        else
        {
            return await HandleVideoRequest(invoker, input, async (inv, videoInfo, partInfo) =>
            {
                var playlistItem = new PlaylistItem
                {
                    Bvid = videoInfo.Bvid,
                    Cid = partInfo.Cid,
                    Title = videoInfo.Title,
                    Uploader = videoInfo.Uploader,
                    CoverUrl = videoInfo.CoverUrl,
                    PartIndex = partInfo.Index,
                    PartTitle = partInfo.Title,
                    RequesterUid = invoker?.ClientUid.ToString()
                };

                // 计算插入位置
                int insertIndex = (currentTrackIndex < 0) ? BilibiliPlaylist.Count : currentTrackIndex + 1;
                BilibiliPlaylist.Insert(insertIndex, playlistItem);

                // 设置索引并立即播放
                currentTrackIndex = insertIndex - 1;
                await PlayNextTrack(invoker);

                return null;
            });
        }
    }

    [Command("b vp")]
    public async Task<string> BilibiliPlayPart(InvokerData invoker, int partIndex)
    {
        if (lastSearchedVideo == null) return "请先使用 !b v [BV号] 获取视频信息。";
        var partToPlay = lastSearchedVideo.Parts.FirstOrDefault(p => p.Index == partIndex);
        if (partToPlay == null) return $"请输入有效编号（1 - {lastSearchedVideo.Parts.Count}）。";

        var playlistItem = new PlaylistItem
        {
            Bvid = lastSearchedVideo.Bvid,
            Cid = partToPlay.Cid,
            Title = lastSearchedVideo.Title,
            Uploader = lastSearchedVideo.Uploader,
            CoverUrl = lastSearchedVideo.CoverUrl,
            PartIndex = partToPlay.Index,
            PartTitle = partToPlay.Title,
            RequesterUid = invoker?.ClientUid.ToString()
        };

        // 计算插入位置
        int insertIndex = (currentTrackIndex < 0) ? BilibiliPlaylist.Count : currentTrackIndex + 1;
        BilibiliPlaylist.Insert(insertIndex, playlistItem);

        // 设置索引并立即播放
        currentTrackIndex = insertIndex - 1;
        await PlayNextTrack(invoker);

        return null;
    }

    [Command("b vall")]
    public async Task<string> BilibiliPlayAllCommand(InvokerData invoker, string bvid)
    {
        if (string.IsNullOrWhiteSpace(bvid)) return "请提供 BV 号。";
        try
        {
            string viewJson = await http.GetStringAsync($"https://api.bilibili.com/x/web-interface/view?bvid={bvid}");
            JObject viewData = JObject.Parse(viewJson)["data"] as JObject;
            if (viewData?["ugc_season"] == null) return "该视频不属于任何合集。";

            var videoList = ParseSeasonToList(viewData);
            var collectionTitle = viewData["ugc_season"]?["title"]?.ToString() ?? "未知合集";
            if (videoList.Count == 0) return "无法从合集中解析出任何视频。";

            // --- 新的核心逻辑 ---

            // 旋转列表，确保目标bvid是第一个
            int startIndex = videoList.FindIndex(v => v.Bvid == bvid);
            if (startIndex == -1) startIndex = 0;
            var rotatedList = videoList.Skip(startIndex).Concat(videoList.Take(startIndex)).ToList();

            var itemsToInsert = rotatedList.Select(video =>
            {
                var part = video.Parts.First();
                return new PlaylistItem
                {
                    Bvid = video.Bvid,
                    Cid = part.Cid,
                    Title = video.Title,
                    Uploader = video.Uploader,
                    CoverUrl = video.CoverUrl,
                    PartIndex = part.Index,
                    PartTitle = part.Title,
                    RequesterUid = invoker?.ClientUid.ToString()
                };
            }).ToList();

            // 计算插入位置
            int insertIndex = (currentTrackIndex < 0) ? BilibiliPlaylist.Count : currentTrackIndex + 1;
            BilibiliPlaylist.InsertRange(insertIndex, itemsToInsert);

            await _ts3Client.SendChannelMessage($"已将合集《{collectionTitle}》中的 {itemsToInsert.Count} 首歌曲插入队列并立即播放。");

            // 设置索引并立即播放
            currentTrackIndex = insertIndex - 1;
            await PlayNextTrack(invoker);

            return null;
        }
        catch (Exception ex) { return $"播放合集失败: {ex.Message}"; }
    }

    [Command("b add")]
    public async Task<string> BilibiliAddCommand(InvokerData invoker, string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "请提供 BV 号，例如：!b add BV1UT42167xb 或 !b add BV1UT42167xb-3";

        if (input.EndsWith("-a", StringComparison.OrdinalIgnoreCase))
        {
            string bvid = input.Substring(0, input.Length - 2);
            try
            {
                string viewApi = $"https://api.bilibili.com/x/web-interface/view?bvid={bvid}";
                string viewJson = await http.GetStringAsync(viewApi);
                JObject viewData = JObject.Parse(viewJson)["data"] as JObject;

                var videoList = ParsePagesToList(viewData);
                if (videoList.Count == 0)
                    return "未能找到任何分P来添加。";

                var videoTitle = viewData["title"]?.ToString();
                string summaryMessage = $"已将视频《{videoTitle}》的全部 {videoList.Count} 个分P加入播放队列。";

                // 只批量添加，不触发播放
                return await BatchAddAsync(invoker,videoList, summaryMessage);
            }
            catch (Exception ex)
            {
                return $"添加全部分P失败: {ex.Message}";
            }
        }
        else
        {
            // HandleVideoRequest 现在会调用新的 EnqueueAudio 方法
            return await HandleVideoRequest(invoker, input, (inv, videoInfo, partInfo) => EnqueueAudio(inv, videoInfo, partInfo));
        }
    }

    [Command("b addp")]
    public async Task<string> BilibiliAddPart(InvokerData invoker, int partIndex)
    {
        if (lastSearchedVideo == null)
            return "请先使用 !b add [BV号] 获取视频信息。";

        var partToAdd = lastSearchedVideo.Parts.FirstOrDefault(p => p.Index == partIndex);
        if (partToAdd == null)
            return $"请输入有效编号（1 - {lastSearchedVideo.Parts.Count}）。";

        return await EnqueueAudio(invoker, lastSearchedVideo, partToAdd);
    }

    [Command("b addall")]
    public async Task<string> BilibiliAddAllCommand(InvokerData invoker, string bvid)
    {
        if (string.IsNullOrWhiteSpace(bvid))
            return "请提供 BV 号。用法: !b addall [BV号]";

        try
        {
            string viewApi = $"https://api.bilibili.com/x/web-interface/view?bvid={bvid}";
            string viewJson = await http.GetStringAsync(viewApi);
            JObject viewData = JObject.Parse(viewJson)["data"] as JObject;

            if (viewData?["ugc_season"] == null)
                return "该视频不属于任何合集。";

            var videoList = ParseSeasonToList(viewData);
            var collectionTitle = viewData["ugc_season"]?["title"]?.ToString() ?? "未知合集";

            if (videoList.Count == 0)
                return "无法从合集中解析出任何视频。";

            // 核心改动：调用旋转添加，但 startPlaying 参数为 false
            return await BatchAddRotatedAsync(invoker, videoList, bvid, collectionTitle, false);
        }
        catch (Exception ex)
        {
            return $"获取或添加合集失败: {ex.Message}";
        }
    }
    
    [Command("b ls")]
    public async Task CommandListPlaylist(int page = 0)
    {
        if (page > 0)
        {
            // 如果用户指定了页码，则显示该页
            await ShowPlaylistAsync(page);
        }
        else
        {
            // 如果用户未指定页码，则自动显示当前播放歌曲所在的页面
            int currentPage = (currentTrackIndex / 15) + 1;
            await ShowPlaylistAsync(currentPage);
        }
    }

   

    #region Playback Control Commands

    [Command("b mode")]
    public async Task CommandSetMode(string mode)
    {
        string reply;
        switch (mode?.ToLower())
        {
            case "1":            
            case "顺序":
                currentPlayMode = PlayMode.Sequence;
                reply = "bilibili播放模式已设置为：顺序播放 (播完列表后停止)";
                break;
            case "2":           
            case "循环":
                currentPlayMode = PlayMode.Repeat;
                reply = "bilibili播放模式已设置为：列表循环";
                break;
            case "3":           
            case "单曲":
                currentPlayMode = PlayMode.Single;
                reply = "播放模式已设置为：单曲循环";
                break;
            default:
                reply = "无效的模式！可用模式: 顺序（1）,循环（2）, 单曲（3）";
                break;
        }
        await _ts3Client.SendChannelMessage(reply);
    }

    [Command("b next")]
    public async Task CommandNext(InvokerData invoker)
    {
        if (BilibiliPlaylist.Count == 0)
        {
            await _ts3Client.SendChannelMessage("bilist播放列表是空的，没有下一首歌。");
            return;
        }
        await _ts3Client.SendChannelMessage($"已切换到bilist下一首歌...");
        // 直接调用 PlayNextTrack，它会自动处理循环和列表结束的逻辑
        await PlayNextTrack(invoker);
    }

    [Command("b pre")]
    public async Task CommandPrevious(InvokerData invoker)
    {
        if (BilibiliPlaylist.Count < 2)
        {
            await _ts3Client.SendChannelMessage("bilist播放列表中歌曲不足，无法切换到上一首。");
            return;
        }

        await _ts3Client.SendChannelMessage($"已切换到bilist上一首歌...");

        // 核心逻辑：索引回退两格，以抵消 PlayNextTrack 中的 +1
        currentTrackIndex -= 2;

        // 处理边界情况：如果回退后索引变为负数
        if (currentTrackIndex < -1)
        {
            if (currentPlayMode == PlayMode.Repeat)
            {
                // 列表循环模式下，回到列表末尾
                currentTrackIndex = BilibiliPlaylist.Count - 2;
            }
            else
            {
                // 顺序模式下，回到列表开头
                currentTrackIndex = -1;
            }
        }

        await PlayNextTrack(invoker);
    }

    [Command("b go")]
    public async Task CommandGoTo(InvokerData invoker, int? index = null)
    {
        // --- 情况1: 用户输入了 "!b go [编号]" ---
        if (index.HasValue)
        {
            int targetIndex = index.Value;
            if (targetIndex < 1 || targetIndex > BilibiliPlaylist.Count)
            {
                await _ts3Client.SendChannelMessage($"bilist跳转失败！请输入 1 到 {BilibiliPlaylist.Count} 之间的有效索引。");
                return;
            }

            await _ts3Client.SendChannelMessage($"已跳转到bilist第 {targetIndex} 首歌...");

            // 核心逻辑：将索引设置为目标歌曲的前一首，然后调用 PlayNextTrack
            currentTrackIndex = targetIndex - 2;
            await PlayNextTrack(invoker);
        }
        // --- 情况2: 用户只输入了 "!b go" ---
        else
        {
            // a) 如果有歌曲正在播放 (currentTrackIndex >= 0)，则重播当前歌曲
            if (currentTrackIndex >= 0 && currentTrackIndex < BilibiliPlaylist.Count)
            {
                var currentTrack = BilibiliPlaylist[currentTrackIndex];
                await _ts3Client.SendChannelMessage($"正在播放bilist第{currentTrackIndex}首歌"); ;

                currentTrackIndex--;
                await PlayNextTrack(invoker);
            }
            // b) 如果当前没有歌曲播放 (currentTrackIndex == -1)，但播放列表不为空
            else if (BilibiliPlaylist.Count > 0)
            {
                await _ts3Client.SendChannelMessage("开始播放bilist。");
                // PlayNextTrack 会自动将索引从 -1 变为 0 并播放
                await PlayNextTrack(invoker);
            }
            // c) 如果播放列表是空的
            else
            {
                await _ts3Client.SendChannelMessage("bilist是空的，没有可以播放的歌曲。");
            }
        }
    }

    [Command("b remove")]
    public async Task CommandRemove(InvokerData invoker, int index)
    {
        if (index < 1 || index > BilibiliPlaylist.Count)
        {
            await _ts3Client.SendChannelMessage($"bilist移除失败！请输入 1 到 {BilibiliPlaylist.Count} 之间的有效索引。");
            return;
        }

        int removeIndex = index - 1;
        var removedItem = BilibiliPlaylist[removeIndex];
        BilibiliPlaylist.RemoveAt(removeIndex);

        await _ts3Client.SendChannelMessage($"已移除bilist歌曲：{removedItem.Title}");

        if (removeIndex < currentTrackIndex)
        {
            // 如果移除的是当前播放歌曲之前的歌，当前索引需要-1
            currentTrackIndex--;
        }
        else if (removeIndex == currentTrackIndex)
        {
            // 如果移除的正是当前播放的歌曲，则播放下一首
            currentTrackIndex--; // 先-1抵消
            await PlayNextTrack(invoker);
        }
    }

    [Command("b move")]
    public async Task CommandMove(InvokerData invoker, int fromIndex, int toIndex)
    {
        if (fromIndex < 1 || fromIndex > BilibiliPlaylist.Count || toIndex < 1 || toIndex > BilibiliPlaylist.Count)
        {
            await _ts3Client.SendChannelMessage($"bilist移动失败！请输入 1 到 {BilibiliPlaylist.Count} 之间的有效索引。");
            return;
        }

        int oldIdx = fromIndex - 1;
        int newIdx = toIndex - 1;

        var item = BilibiliPlaylist[oldIdx];
        BilibiliPlaylist.RemoveAt(oldIdx);
        BilibiliPlaylist.Insert(newIdx, item);

        // 核心逻辑：更新当前播放索引，以追踪歌曲的移动
        if (currentTrackIndex == oldIdx) // 如果移动的是当前播放的歌曲
        {
            currentTrackIndex = newIdx;
        }
        else if (oldIdx < currentTrackIndex && newIdx >= currentTrackIndex) // 从前往后移，经过了当前歌曲
        {
            currentTrackIndex--;
        }
        else if (oldIdx > currentTrackIndex && newIdx <= currentTrackIndex) // 从后往前移，经过了当前歌曲
        {
            currentTrackIndex++;
        }

        await _ts3Client.SendChannelMessage($"已将bilist第 {fromIndex} 首歌移动到了第 {toIndex} 位。");
    }

    // --- 新增代码块 开始 (清空队列指令) ---

    [Command("b clear")]
    public async Task CommandClearPlaylist(InvokerData invoker)
    {
        if (BilibiliPlaylist.Count == 0)
        {
            await _ts3Client.SendChannelMessage("播放列表已经是空的了。");
            return;
        }

        // 1. 停止当前播放
        await _playManager.Stop();

        // 2. 清空播放列表
        BilibiliPlaylist.Clear();

        // 3. 重置所有播放状态
        currentTrackIndex = -1;
        isPlayingBilibili = false;

        // 4. 将机器人状态恢复默认
        await _ts3Client.ChangeName(defaultBotName);

        if (!System.IO.File.Exists("/.dockerenv"))
        {
            // 如果不是Docker环境，则删除头像
            await _ts3Client.DeleteAvatar();
        }

        await _ts3Client.SendChannelMessage("Bilibili 播放列表已清空。");
    }

    // --- 新增代码块 结束 ---

    #endregion



    #endregion
}
