using ImmichFrame.Core.Api;
using ImmichFrame.Core.Interfaces;

namespace ImmichFrame.Core.Logic.Pool;

public abstract class CachingApiAssetsPool(IApiCache apiCache, ImmichApi immichApi, IAccountSettings accountSettings) : IAssetPool
{
    private readonly Random _random = new();
    
    public async Task<long> GetAssetCount(CancellationToken ct = default)
    {
        return (await AllAssets(ct)).Count();
    }
    
    public async Task<IEnumerable<AssetResponseDto>> GetAssets(int requested, CancellationToken ct = default)
    {
        var assets = await AllAssets(ct);
        var recencyBias = accountSettings.RecencyBias ?? 0.0;

        if (recencyBias == 0)
        {
            return assets.OrderBy(_ => _random.Next()).Take(requested);
        }

        return ApplyRecencyBias(assets, requested, recencyBias);
    }

    private async Task<IEnumerable<AssetResponseDto>> AllAssets(CancellationToken ct = default)
    {
        return await apiCache.GetOrAddAsync(GetType().FullName!, () => ApplyAccountFilters(LoadAssets(ct)));
    }

    private IEnumerable<AssetResponseDto> ApplyRecencyBias(
        IEnumerable<AssetResponseDto> assets,
        int requested,
        double recencyBias)
    {
        var today = DateTime.Today;
        var assetList = assets.ToList();

        // Calculate weights based on recency
        var weightedAssets = assetList.Select(asset =>
        {
            var date = asset.ExifInfo?.DateTimeOriginal ?? asset.FileCreatedAt;
            var daysAgo = Math.Max(0, (today - date).TotalDays);
            // Newer photos get higher weights
            // Using exponential decay: weight = exp(-bias * daysAgo / 365.0)
            // At bias=1.0, photos from 1 year ago have ~37% weight of today's photos
            var weight = Math.Exp(-recencyBias * daysAgo / 365.0);
            return (Asset: asset, Weight: weight);
        }).ToList();

        // Normalize weights
        var totalWeight = weightedAssets.Sum(x => x.Weight);

        // Weighted random selection without replacement
        var selected = new List<AssetResponseDto>();

        for (int i = 0; i < requested && weightedAssets.Count > 0; i++)
        {
            var point = _random.NextDouble() * totalWeight;
            var cumulative = 0.0;

            for (int j = 0; j < weightedAssets.Count; j++)
            {
                cumulative += weightedAssets[j].Weight;
                if (point <= cumulative)
                {
                    selected.Add(weightedAssets[j].Asset);
                    totalWeight -= weightedAssets[j].Weight;
                    weightedAssets.RemoveAt(j);
                    break;
                }
            }
        }

        return selected;
    }


    protected async Task<IEnumerable<AssetResponseDto>> ApplyAccountFilters(Task<IEnumerable<AssetResponseDto>> unfiltered)
    {
        // Display only Images
        var assets = (await unfiltered).Where(x => x.Type == AssetTypeEnum.IMAGE);

        if (!accountSettings.ShowArchived)
            assets = assets.Where(x => x.IsArchived == false);

        var takenBefore = accountSettings.ImagesUntilDate.HasValue ? accountSettings.ImagesUntilDate : null;
        if (takenBefore.HasValue)
        {
            assets = assets.Where(x => x.ExifInfo.DateTimeOriginal <= takenBefore);
        }

        var takenAfter = accountSettings.ImagesFromDate.HasValue ? accountSettings.ImagesFromDate : accountSettings.ImagesFromDays.HasValue ? DateTime.Today.AddDays(-accountSettings.ImagesFromDays.Value) : null;
        if (takenAfter.HasValue)
        {
            assets = assets.Where(x => x.ExifInfo.DateTimeOriginal >= takenAfter);
        }

        if (accountSettings.Rating is int rating)
        {
            assets = assets.Where(x => x.ExifInfo.Rating == rating);
        }
        
        return assets;
    }
        
    protected abstract Task<IEnumerable<AssetResponseDto>> LoadAssets(CancellationToken ct = default);
}