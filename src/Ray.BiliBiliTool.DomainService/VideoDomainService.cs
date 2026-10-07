using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ray.BiliBiliTool.Agent;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Dtos;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Dtos.ApiApi.Daily;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Dtos.ApiApi.Relation;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Dtos.ApiApi.Video;
using Ray.BiliBiliTool.Agent.BiliBiliAgent.Interfaces;
using Ray.BiliBiliTool.Config.Options;
using Ray.BiliBiliTool.Domain;
using Ray.BiliBiliTool.Domain.Exceptions;
using Ray.BiliBiliTool.DomainService.Dtos;
using Ray.BiliBiliTool.DomainService.Interfaces;

namespace Ray.BiliBiliTool.DomainService;

/// <summary>
/// 视频
/// </summary>
public class VideoDomainService(
    ILogger<VideoDomainService> logger,
    IOptionsMonitor<DailyTaskOptions> dailyTaskOptions,
    IApiApi apiApi,
    TimeProvider? clock = null
) : IVideoDomainService
{
    private readonly DailyTaskOptions _dailyTaskOptions = dailyTaskOptions.CurrentValue;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<string, int> _expDic = Config.Constants.ExpDic;

    /// <summary>
    /// 获取视频详情
    /// </summary>
    /// <param name="aid"></param>
    /// <returns></returns>
    public Task<VideoDetail> GetVideoDetail(string aid) => GetVideoDetailCore(aid, null);

    public Task<VideoDetail> GetVideoDetail(string aid, BiliCookie ck) =>
        GetVideoDetailCore(aid, ck.ToString());

    private async Task<VideoDetail> GetVideoDetailCore(string aid, string? cookie)
    {
        var re = await apiApi.GetVideoDetail(aid, cookie);
        if (re.Code != 0)
            throw new BiliBusinessException($"获取视频详情失败：{re.Message}({re.Code})");
        return re.Data ?? throw new BiliBusinessException("获取视频详情失败：B 站未返回视频信息");
    }

    /// <summary>
    /// 从排行榜获取随机视频
    /// </summary>
    /// <returns></returns>
    public async Task<RankingInfo> GetRandomVideoOfRanking()
    {
        var apiResponse = await apiApi.GetRegionRankingVideosV2();
        if (apiResponse.Code != 0)
        {
            throw new BiliBusinessException(
                $"获取排行榜失败：{apiResponse.Message}({apiResponse.Code})"
            );
        }

        if (apiResponse.Data?.List is not { Count: > 0 })
            throw new BiliBusinessException("获取排行榜失败：B 站未返回可用视频");

        logger.LogDebug("获取排行榜成功");
        var data = apiResponse.Data.List[new Random().Next(apiResponse.Data.List.Count)];
        return data;
    }

    public async Task<UpVideoInfo?> GetRandomVideoOfUp(long upId, int total, BiliCookie ck)
    {
        if (total <= 0)
            return null;

        var req = new SearchVideosByUpIdDto()
        {
            mid = upId,
            ps = 1,
            pn = new Random().Next(1, total + 1),
        };

        BiliApiResponse<SearchUpVideosResponse> re = await apiApi.SearchVideosByUpId(
            req,
            ck.ToString()
        );

        if (re.Code != 0)
        {
            throw new BiliBusinessException($"获取主播视频失败：{re.Message}({re.Code})");
        }

        return re.Data?.List?.Vlist.FirstOrDefault();
    }

    /// <summary>
    /// 获取UP主的视频总数量
    /// </summary>
    /// <param name="upId"></param>
    /// <returns></returns>
    public async Task<int> GetVideoCountOfUp(long upId, BiliCookie ck)
    {
        var req = new SearchVideosByUpIdDto() { mid = upId };

        BiliApiResponse<SearchUpVideosResponse> re = await apiApi.SearchVideosByUpId(
            req,
            ck.ToString()
        );
        if (re.Code != 0)
        {
            throw new BiliBusinessException($"获取主播视频数量失败：{re.Message}({re.Code})");
        }

        if (re.Data?.Page is null)
            throw new BiliBusinessException("获取主播视频数量失败：B 站未返回分页信息");

        return re.Data.Page.Count;
    }

    public async Task WatchAndShareVideo(DailyTaskInfo dailyTaskStatus, BiliCookie ck)
    {
        VideoInfoDto? targetVideo = null;
        var needsWatch = !dailyTaskStatus.Watch && _dailyTaskOptions.IsWatchVideo;
        var needsShare = !dailyTaskStatus.Share && _dailyTaskOptions.IsShareVideo;

        //至少有一项未完成，获取视频
        if (needsWatch || needsShare)
        {
            targetVideo = await GetRandomVideoForWatchAndShare(ck);
            logger.LogInformation("【随机视频】{title}", targetVideo.Title);
        }

        bool watched = false;
        //观看
        if (needsWatch)
        {
            await WatchVideo(targetVideo!, ck);
            watched = true;
        }
        else
            logger.LogInformation("今天已经观看过了，不需要再看啦");

        //分享
        if (needsShare)
        {
            //如果没有打开观看过，则分享前先打开视频
            if (!watched)
            {
                try
                {
                    await OpenVideo(targetVideo!, ck);
                }
                catch (Exception e)
                {
                    //ignore
                    logger.LogError("打开视频异常：{msg}", e.Message);
                }
            }
            await ShareVideo(targetVideo!, ck);
        }
        else
            logger.LogInformation("今天已经分享过了，不用再分享啦");
    }

    /// <summary>
    /// 观看视频
    /// </summary>
    public async Task WatchVideo(VideoInfoDto videoInfo, BiliCookie ck)
    {
        //开始上报一次
        await OpenVideo(videoInfo, ck);

        //结束上报一次
        videoInfo.Duration = videoInfo.Duration ?? 15;
        int max = videoInfo.Duration < 15 ? videoInfo.Duration.Value : 15;
        int playedTime = new Random().Next(1, max);

        var request = new UploadVideoHeartbeatRequest
        {
            Aid = long.Parse(videoInfo.Aid),
            Bvid = videoInfo.Bvid,
            Cid = videoInfo.Cid,
            Mid = long.Parse(ck.UserId),
            Csrf = ck.BiliJct,

            Played_time = playedTime,
            Realtime = playedTime,
            Real_played_time = playedTime,
        };
        BiliApiResponse apiResponse = await apiApi.UploadVideoHeartbeat(
            request.Aid,
            request.Played_time,
            request,
            ck.ToString()
        );

        if (apiResponse.Code == 0)
        {
            _expDic.TryGetValue("每日观看视频", out int exp);
            logger.LogInformation(
                "视频播放成功，已观看到第{playedTime}秒，经验+{exp} √",
                playedTime,
                exp
            );
        }
        else
        {
            logger.LogError("视频播放失败，原因：{msg}", apiResponse.Message);
            throw new BiliBusinessException(
                $"视频观看被 B 站拒绝，错误码 {apiResponse.Code}：{apiResponse.Message}"
            );
        }
        TaskRecoveryProgressScope.Report(
            "video",
            "观看视频",
            TaskRecoveryProgressState.Completed,
            "观看记录已提交"
        );
    }

    /// <summary>
    /// 分享视频
    /// </summary>
    /// <param name="videoInfo">视频</param>
    public async Task ShareVideo(VideoInfoDto videoInfo, BiliCookie ck)
    {
        var aid = long.Parse(videoInfo.Aid);
        var cid = videoInfo.Cid;
        if (cid <= 0)
        {
            var videoDetail = await GetVideoDetail(videoInfo.Aid, ck);
            if (videoDetail.Aid != aid || videoDetail.Cid <= 0)
                throw new BiliBusinessException("分享视频信息不完整，请重新获取视频");
            cid = videoDetail.Cid;
        }
        var request = new ShareVideoCompletionRequest(
            aid,
            cid,
            ck.BiliJct,
            _clock.GetUtcNow().ToUnixTimeSeconds()
        );
        BiliApiResponse apiResponse = await apiApi.CompleteVideoShare(request, ck.ToString());

        if (apiResponse.Code is 0 or 71000)
        {
            logger.LogInformation("分享记录已提交，正在核对 B 站今日分享进度");
        }
        else
        {
            logger.LogError("视频分享失败，原因: {msg}", apiResponse.Message);
            throw new BiliBusinessException(
                $"视频分享被 B 站拒绝，错误码 {apiResponse.Code}：{apiResponse.Message}"
            );
        }
        TaskRecoveryProgressScope.Report(
            "video",
            "分享视频",
            TaskRecoveryProgressState.Running,
            "分享记录已提交，正在确认今日进度"
        );
        var queryFailed = false;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(TimeSpan.FromSeconds(attempt * 3), _clock);
            try
            {
                var reward = await apiApi.GetDailyTaskRewardInfoAsync(ck.ToString());
                if (reward.Code == 0 && reward.Data?.Share == true)
                {
                    _expDic.TryGetValue("每日分享视频", out int exp);
                    logger.LogInformation("B 站已确认今日分享完成，经验+{exp} √", exp);
                    TaskRecoveryProgressScope.Report(
                        "video",
                        "分享视频",
                        TaskRecoveryProgressState.Completed,
                        "B 站已确认今日分享完成"
                    );
                    return;
                }
                queryFailed = reward.Code != 0 || reward.Data is null;
                if (queryFailed)
                    break;
            }
            catch (Exception error)
                when (error
                        is HttpRequestException
                            or Refit.ApiException
                            or System.Text.Json.JsonException
                            or TimeoutException
                )
            {
                queryFailed = true;
                logger.LogWarning(
                    "分享进度查询未完成：{reason}",
                    TaskRecoveryProgressScope.DescribeFailure(error)
                );
                break;
            }
        }
        var detail = queryFailed
            ? "分享记录已提交，今日进度暂未获取"
            : "分享记录已提交，B 站尚未确认今日完成";
        logger.LogInformation("{detail}", detail);
        TaskRecoveryProgressScope.Report(
            "video",
            "分享视频",
            TaskRecoveryProgressState.Pending,
            detail
        );
    }

    /// <summary>
    /// 模拟打开视频播放（初始上报一次进度）
    /// </summary>
    /// <param name="videoInfo"></param>
    /// <returns></returns>
    public async Task<bool> OpenVideo(VideoInfoDto videoInfo, BiliCookie ck)
    {
        var request = new UploadVideoHeartbeatRequest
        {
            Aid = long.Parse(videoInfo.Aid),
            Bvid = videoInfo.Bvid,
            Cid = videoInfo.Cid,

            Mid = long.Parse(ck.UserId),
            Csrf = ck.BiliJct,
        };

        //开始上报一次
        BiliApiResponse apiResponse = await apiApi.UploadVideoHeartbeat(
            request.Aid,
            request.Played_time,
            request,
            ck.ToString()
        );

        if (apiResponse.Code == 0)
        {
            logger.LogDebug("打开视频成功");
            return true;
        }
        else
        {
            logger.LogError("视频打开失败，原因：{msg}", apiResponse.Message);
            return false;
        }
    }

    #region private
    /// <summary>
    /// 获取一个视频用来观看并分享
    /// </summary>
    /// <returns></returns>
    public async Task<VideoInfoDto> GetRandomVideoForWatchAndShare(BiliCookie ck)
    {
        var configuredUps = _dailyTaskOptions.SupportUpIdList;
        if (configuredUps.Count > 0)
        {
            var configured = await VideoSourceSelection.TryAsync(
                () => GetRandomVideoOfUps(configuredUps, ck),
                logger,
                "配置主播"
            );
            if (configured is not null)
                return configured;
        }

        var video = await VideoSourceSelection.TryAsync(
            () => GetRandomVideoOfFollowingUps(ck),
            logger,
            "关注主播"
        );
        if (video != null)
            return video;

        //然后从排行榜中取
        var t = await GetRandomVideoOfRanking();
        return new VideoInfoDto
        {
            Aid = t.Aid.ToString(),
            Bvid = t.Bvid,
            Cid = t.Cid,
            Copyright = t.Copyright,
            Duration = t.Duration,
            Title = t.Title,
        };
    }

    private async Task<VideoInfoDto?> GetRandomVideoOfFollowingUps(BiliCookie ck)
    {
        //关注列表
        var request = new GetFollowingsRequest(long.Parse(ck.UserId));
        BiliApiResponse<GetFollowingsResponse> result = await apiApi.GetFollowings(
            request,
            ck.ToString()
        );
        if (result.Code != 0)
            throw new BiliBusinessException($"获取关注列表失败：{result.Message}({result.Code})");

        if (result.Data is not null && result.Data.Total > 0)
        {
            var video = await GetRandomVideoOfUps(result.Data.List.Select(x => x.Mid).ToList(), ck);
            if (video != null)
                return video;
        }

        return null;
    }

    /// <summary>
    /// 从up集合中获取一个随机视频
    /// </summary>
    /// <param name="upIds"></param>
    /// <returns></returns>
    private async Task<VideoInfoDto?> GetRandomVideoOfUps(List<long> upIds, BiliCookie ck)
    {
        var candidates = upIds.Where(id => id > 0).ToArray();
        if (candidates.Length == 0)
            return null;

        long upId = candidates[new Random().Next(0, candidates.Length)];

        int count = await GetVideoCountOfUp(upId, ck);

        if (count > 0)
        {
            var video = await GetRandomVideoOfUp(upId, count, ck);
            if (video == null)
                return null;
            return new VideoInfoDto
            {
                Aid = video.Aid.ToString(),
                Bvid = video.Bvid,
                //Cid=,
                //Copyright=
                Title = video.Title,
                Duration = video.Duration,
            };
        }

        return null;
    }
    #endregion private
}
