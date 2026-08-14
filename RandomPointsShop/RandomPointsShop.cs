using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Plugins.Interfaces;
using ArchiSteamFarm.Steam;
using ArchiSteamFarm.Steam.Integration;
using ArchiSteamFarm.Web.Responses;
using JetBrains.Annotations;
using SteamKit2;

namespace RandomPointsShop;

#pragma warning disable CA1812 // ASF uses this class during runtime
#pragma warning disable CA1001 // Plugin instances live for the process' lifetime; ASF gives IPlugin implementations no disposal hook to call into
#pragma warning disable CA5394 // Randomness here only picks an arbitrary candidate/delay, it's not used for anything security-sensitive
[UsedImplicitly]
internal sealed class RandomPointsShop : IASF, IBotConnection, IGitHubPluginUpdates {
	// Steam's own community_item_class values (IPlayerService/ILoyaltyRewardsService), confirmed against
	// Gobot1234/steam.py's enums.py: ProfileBackground=3, Emoticon=4, MiniProfileBackground=13, AvatarFrame=14.
	// Deliberately excludes Badge(1)/GameCard(2)/AnimatedAvatar(15)/SteamDeckKeyboardSkin(16) - animated avatars
	// conflict with RandomProfileAvatar (same slot), badges have no verified discovery API (see RandomFavoriteBadge
	// research), and keyboard skins are Steam Deck-only cosmetics with no bearing on a farming bot's profile.
	private static readonly int[] DefaultAllowedItemClasses = [3, 4, 13, 14];

	private const byte DefaultCheapestCandidateCount = 10;
	private const ushort DefaultCatalogCacheHours = 6;
	private const ushort DefaultMaxDelayInHours = 168;
	private const ushort DefaultMinDelayInHours = 24;

	// Bundle items (type 6) resolve into nested defids with their own redemption flow (see DevSplash/FreePointsShop's
	// QueryBundleItems) - out of scope here, this plugin only spends on single items.
	private const int BundleType = 6;

	private static readonly Uri SteamApiURL = new("https://api.steampowered.com");
	private static readonly Uri SteamStoreURL = new("https://store.steampowered.com");
	private static readonly Uri PointsShopReferer = new(SteamStoreURL, "/points/shop");

	private readonly ConcurrentDictionary<string, bool> BotAllOwnedWarned = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, CancellationTokenSource> BotLoops = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, bool> BotNoEligibleCandidatesWarned = new(StringComparer.Ordinal);

	// Serializes concurrent refreshes of CatalogCache so multiple bots hitting an expired cache at once don't all fetch it in parallel
	private readonly SemaphoreSlim CatalogLock = new(1, 1);

	private int[] AllowedItemClasses = DefaultAllowedItemClasses;
	private (DateTime FetchedAt, List<RewardItemRecord> Items) CatalogCache = (DateTime.MinValue, []);
	private ushort CatalogCacheHours = DefaultCatalogCacheHours;
	private byte CheapestCandidateCount = DefaultCheapestCandidateCount;
	private bool Enabled;
	private ushort MaxDelayInHours = DefaultMaxDelayInHours;
	private ushort MinDelayInHours = DefaultMinDelayInHours;

	public string Name => nameof(RandomPointsShop);
	public string RepositoryName => "buddymurdock/ASF-RandomPointsShop";
	public Version Version => typeof(RandomPointsShop).Assembly.GetName().Version ?? throw new InvalidOperationException(nameof(Version));

	// Reads RandomPointsShopEnabled / RandomPointsShopMinDelayHours / RandomPointsShopMaxDelayHours /
	// RandomPointsShopCatalogCacheHours / RandomPointsShopAllowedItemClasses / RandomPointsShopCheapestCandidateCount
	// from the global ASF.json config
	public Task OnASFInit(IReadOnlyDictionary<string, JsonElement>? additionalConfigProperties = null) {
		if (additionalConfigProperties != null) {
			foreach ((string configProperty, JsonElement configValue) in additionalConfigProperties) {
				switch (configProperty) {
					case $"{nameof(RandomPointsShop)}Enabled" when configValue.ValueKind is JsonValueKind.True or JsonValueKind.False:
						Enabled = configValue.GetBoolean();

						break;
					case $"{nameof(RandomPointsShop)}MinDelayHours" when (configValue.ValueKind == JsonValueKind.Number) && configValue.TryGetUInt16(out ushort minDelay) && (minDelay > 0):
						MinDelayInHours = minDelay;

						break;
					case $"{nameof(RandomPointsShop)}MaxDelayHours" when (configValue.ValueKind == JsonValueKind.Number) && configValue.TryGetUInt16(out ushort maxDelay) && (maxDelay > 0):
						MaxDelayInHours = maxDelay;

						break;
					case $"{nameof(RandomPointsShop)}CatalogCacheHours" when (configValue.ValueKind == JsonValueKind.Number) && configValue.TryGetUInt16(out ushort cacheHours) && (cacheHours > 0):
						CatalogCacheHours = cacheHours;

						break;
					case $"{nameof(RandomPointsShop)}CheapestCandidateCount" when (configValue.ValueKind == JsonValueKind.Number) && configValue.TryGetByte(out byte candidateCount) && (candidateCount > 0):
						CheapestCandidateCount = candidateCount;

						break;
					case $"{nameof(RandomPointsShop)}AllowedItemClasses" when configValue.ValueKind == JsonValueKind.Array:
						List<int> classes = [];

						foreach (JsonElement element in configValue.EnumerateArray()) {
							if ((element.ValueKind == JsonValueKind.Number) && element.TryGetInt32(out int itemClass)) {
								classes.Add(itemClass);
							}
						}

						if (classes.Count > 0) {
							AllowedItemClasses = [.. classes];
						}

						break;
				}
			}
		}

		if (MinDelayInHours > MaxDelayInHours) {
			(MinDelayInHours, MaxDelayInHours) = (MaxDelayInHours, MinDelayInHours);
		}

		if (!Enabled) {
			ASF.ArchiLogger.LogGenericInfo($"{Name} is disabled, set {nameof(RandomPointsShop)}Enabled to true in ASF.json to turn it on.");

			return Task.CompletedTask;
		}

		ASF.ArchiLogger.LogGenericInfo($"{Name} is enabled, every {MinDelayInHours}-{MaxDelayInHours} hours each bot tries to redeem one random affordable-looking Points Shop item (classes: {string.Join(',', AllowedItemClasses)}).");

		return Task.CompletedTask;
	}

	public Task OnLoaded() {
		ASF.ArchiLogger.LogGenericInfo($"{Name} has been loaded!");

		return Task.CompletedTask;
	}

	public async Task OnBotDisconnected(Bot bot, EResult reason) {
		if (BotLoops.TryRemove(bot.BotName, out CancellationTokenSource? cts)) {
			await cts.CancelAsync().ConfigureAwait(false);
			cts.Dispose();
		}
	}

	public Task OnBotLoggedOn(Bot bot) {
		if (!Enabled) {
			return Task.CompletedTask;
		}

		CancellationTokenSource cts = new();

		if (!BotLoops.TryAdd(bot.BotName, cts)) {
			// A loop for this bot is already running, nothing to do
			cts.Dispose();

			return Task.CompletedTask;
		}

		Utilities.InBackground(() => BotPointsShopLoopAsync(bot, cts.Token), true);

		return Task.CompletedTask;
	}

	// Unlike RandomWishlistAdditions/RandomFollows (finite per-bot target, loop exits once reached), this loop
	// never terminates - points regenerate continuously from card drops/badge crafting, so spending is an
	// ongoing background behavior for the lifetime of the bot's session, same shape as RandomBotTrades/RandomBotComments.
	private async Task BotPointsShopLoopAsync(Bot bot, CancellationToken cancellationToken) {
		while (!cancellationToken.IsCancellationRequested) {
			TimeSpan delay = GetRandomDelay(MinDelayInHours, MaxDelayInHours);

			try {
				await LongDelayAsync(delay, cancellationToken).ConfigureAwait(false);
			} catch (OperationCanceledException) {
				break;
			}

			if (cancellationToken.IsCancellationRequested || !bot.IsConnectedAndLoggedOn) {
				break;
			}

			try {
				await TryRedeemRandomItemAsync(bot).ConfigureAwait(false);
			} catch (Exception e) {
				ASF.ArchiLogger.LogGenericException(e);
			}
		}
	}

	// Task.Delay's underlying timer caps out at ~49.7 days (uint.MaxValue-1 ms) - a delay past that throws
	// ArgumentOutOfRangeException synchronously, which would go unhandled here and crash the entire ASF process
	// via OnUnobservedTaskException (this exact bug hit RandomNickname/RandomProfileAvatar/RandomProfileBackground
	// in production). Chunking sidesteps the limit for arbitrarily long delays. MaxDelayInHours defaults to 168
	// (a week), well under the cap, but a misconfigured ASF.json could push it past ~1194 hours - chunk anyway.
	private static async Task LongDelayAsync(TimeSpan delay, CancellationToken cancellationToken) {
		TimeSpan chunk = TimeSpan.FromDays(1);

		while (delay > chunk) {
			await Task.Delay(chunk, cancellationToken).ConfigureAwait(false);
			delay -= chunk;
		}

		if (delay > TimeSpan.Zero) {
			await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
		}
	}

	// Real people don't wait a uniformly random amount of time between actions - intervals tend to cluster
	// around a typical gap with occasional much shorter/longer ones (bursty/heavy-tailed), not spread flat
	// across [min, max]. Log-normal captures that: min/max become the ~5th/95th percentiles rather than hard
	// bounds, with sqrt(min*max) as the median. z is clamped before use because extreme (min, max) ratios
	// produce a large sigma - an un-clamped Box-Muller tail can drive Math.Exp()/TimeSpan construction into
	// Infinity/OverflowException. The final Math.Clamp is a second, independent safety net on the result itself.
	private static TimeSpan GetRandomDelay(ushort minHours, ushort maxHours) {
		if (minHours == maxHours) {
			return TimeSpan.FromHours(minHours);
		}

		double median = Math.Sqrt((double) minHours * maxHours);
		double sigma = Math.Log((double) maxHours / minHours) / (2 * 1.645);

		double u1 = 1.0 - Random.Shared.NextDouble();
		double u2 = Random.Shared.NextDouble();
		double z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);

		z = Math.Clamp(z, -3.5, 3.5);

		double hours = median * Math.Exp(sigma * z);
		hours = Math.Clamp(hours, minHours / 10.0, maxHours * 5.0);

		return TimeSpan.FromHours(hours);
	}

	private async Task TryRedeemRandomItemAsync(Bot bot) {
		string? token = bot.AccessToken;

		if (string.IsNullOrEmpty(token)) {
			bot.ArchiLogger.LogGenericWarning($"{Name}: bot has no access token yet, skipping this attempt.");

			return;
		}

		List<RewardItemRecord> catalog = await GetCatalogAsync(bot).ConfigureAwait(false);

		List<RewardItemRecord> candidates = [
			.. catalog.Where(
				item => (item.Active == true) &&
					(item.Type != BundleType) &&
					(item.AppID != null) &&
					(item.DefID != null) &&
					TryGetPointCost(item, out _)
			)
		];

		if (candidates.Count == 0) {
			if (BotNoEligibleCandidatesWarned.TryAdd(bot.BotName, true)) {
				bot.ArchiLogger.LogGenericInfo($"{Name}: no eligible Points Shop items in the current catalog for the configured item classes; will keep retrying as the catalog changes.");
			}

			return;
		}

		HashSet<uint> candidateAppIDs = [.. candidates.Select(static item => item.AppID!.Value)];
		HashSet<(uint AppID, int ItemType)> owned = await GetOwnedItemsAsync(bot, token, candidateAppIDs).ConfigureAwait(false);

		List<RewardItemRecord> notOwned = [.. candidates.Where(item => !owned.Contains((item.AppID!.Value, item.CommunityItemType ?? -1)))];

		if (notOwned.Count == 0) {
			if (BotAllOwnedWarned.TryAdd(bot.BotName, true)) {
				bot.ArchiLogger.LogGenericInfo($"{Name}: this bot already owns every eligible item in the current catalog; will keep retrying as the catalog changes.");
			}

			return;
		}

		BotNoEligibleCandidatesWarned.TryRemove(bot.BotName, out _);
		BotAllOwnedWarned.TryRemove(bot.BotName, out _);

		// No pre-check of the bot's points balance (GetSummary has no verified reference implementation, see
		// RandomFavoriteBadge research) - instead bias toward the cheapest candidates to maximize the chance
		// RedeemPoints succeeds, and treat a failed redemption (insufficient balance or item no longer active)
		// as a normal, silently-retried-next-tick outcome rather than something to detect in advance.
		List<RewardItemRecord> cheapest = [
			.. notOwned
				.Select(static item => (Item: item, Cost: TryGetPointCost(item, out long cost) ? cost : long.MaxValue))
				.OrderBy(static entry => entry.Cost)
				.Take(CheapestCandidateCount)
				.Select(static entry => entry.Item)
		];

		RewardItemRecord chosen = cheapest[Random.Shared.Next(cheapest.Count)];

		TryGetPointCost(chosen, out long pointCost);

		bool success = await RedeemPointsAsync(bot, token, chosen.DefID!.Value, pointCost).ConfigureAwait(false);

		if (success) {
			bot.ArchiLogger.LogGenericInfo($"{Name}: redeemed Points Shop item {chosen.DefID} for {pointCost} points.");
		} else {
			bot.ArchiLogger.LogGenericWarning($"{Name}: failed to redeem Points Shop item {chosen.DefID} ({pointCost} points) - likely insufficient balance, or the item is no longer available.");
		}
	}

	private static bool TryGetPointCost(RewardItemRecord item, out long cost) {
		cost = 0;

		return !string.IsNullOrEmpty(item.PointCost) && long.TryParse(item.PointCost, NumberStyles.Integer, CultureInfo.InvariantCulture, out cost) && (cost > 0);
	}

	private async Task<List<RewardItemRecord>> GetCatalogAsync(Bot bot) {
		(DateTime FetchedAt, List<RewardItemRecord> Items) cached = CatalogCache;

		if ((cached.Items.Count > 0) && ((DateTime.UtcNow - cached.FetchedAt) < TimeSpan.FromHours(CatalogCacheHours))) {
			return cached.Items;
		}

		await CatalogLock.WaitAsync().ConfigureAwait(false);

		try {
			// Re-check after acquiring the lock - another bot may have already refreshed it while we were waiting
			cached = CatalogCache;

			if ((cached.Items.Count > 0) && ((DateTime.UtcNow - cached.FetchedAt) < TimeSpan.FromHours(CatalogCacheHours))) {
				return cached.Items;
			}

			List<RewardItemRecord> fresh = await RefreshCatalogAsync(bot).ConfigureAwait(false);

			if (fresh.Count == 0) {
				// The fetch failed or returned nothing useful - keep serving whatever we had before (possibly empty) rather than blocking every bot on a hard failure
				return CatalogCache.Items;
			}

			CatalogCache = (DateTime.UtcNow, fresh);

			return CatalogCache.Items;
		} finally {
			CatalogLock.Release();
		}
	}

	// The Points Shop catalog is Steam's public storefront data (no session/access_token required for the query
	// itself, confirmed via DevSplash/FreePointsShop's QueryRewardItems using the unauthenticated ASF.WebBrowser) -
	// same shape here, just routed through a bot's WebBrowser instance for consistency with the rest of this plugin family.
	private async Task<List<RewardItemRecord>> RefreshCatalogAsync(Bot bot) {
		List<RewardItemRecord> items = [];
		HashSet<uint> seenDefIDs = [];
		string? cursor = null;

		// Safety cap against runaway pagination if Steam ever returns a cursor loop; 20 pages * 200 items is far more than AllowedItemClasses realistically needs
		for (int page = 0; page < 20; page++) {
			List<string> query = ["count=200"];

			for (int i = 0; i < AllowedItemClasses.Length; i++) {
				query.Add($"community_item_classes[{i}]={AllowedItemClasses[i]}");
			}

			if (!string.IsNullOrEmpty(cursor)) {
				query.Add($"cursor={Uri.EscapeDataString(cursor)}");
			}

			Uri request = new(SteamApiURL, $"/ILoyaltyRewardsService/QueryRewardItems/v1/?{string.Join('&', query)}");

			ObjectResponse<RewardItemsEnvelope>? response = await bot.ArchiWebHandler.WebBrowser.UrlGetToJsonObject<RewardItemsEnvelope>(request, referer: PointsShopReferer).ConfigureAwait(false);
			RewardItemsData? data = response?.Content?.Response;

			if ((data?.Definitions == null) || (data.Definitions.Count == 0)) {
				break;
			}

			foreach (RewardItemRecord item in data.Definitions) {
				if ((item.DefID != null) && seenDefIDs.Add(item.DefID.Value)) {
					items.Add(item);
				}
			}

			if (string.IsNullOrEmpty(data.NextCursor) || string.Equals(data.NextCursor, cursor, StringComparison.Ordinal)) {
				break;
			}

			cursor = data.NextCursor;
		}

		return items;
	}

	// Confirmed against DevSplash/FreePointsShop's GetCommunityInventory: IQuestService/GetCommunityInventory
	// returns everything the bot owns of the requested appids' community items (regardless of class), matched
	// against a catalog entry by (appid, community_item_type) - the same pairing FreePointsShop itself uses to
	// avoid re-redeeming an item the bot already has.
	private static async Task<HashSet<(uint AppID, int ItemType)>> GetOwnedItemsAsync(Bot bot, string token, HashSet<uint> appIDs) {
		if (appIDs.Count == 0) {
			return [];
		}

		List<string> query = [$"access_token={token}"];
		int i = 0;

		foreach (uint appID in appIDs) {
			query.Add($"filter_appids[{i}]={appID}");
			i++;
		}

		Uri request = new(SteamApiURL, $"/IQuestService/GetCommunityInventory/v1/?{string.Join('&', query)}");

		ObjectResponse<CommunityInventoryEnvelope>? response = await bot.ArchiWebHandler.UrlGetToJsonObjectWithSession<CommunityInventoryEnvelope>(request, referer: PointsShopReferer).ConfigureAwait(false);

		HashSet<(uint, int)> owned = [];

		foreach (CommunityInventoryItem item in response?.Content?.Response?.Items ?? []) {
			if ((item.AppID != null) && (item.ItemType != null)) {
				owned.Add((item.AppID.Value, item.ItemType.Value));
			}
		}

		return owned;
	}

	// Confirmed against DevSplash/FreePointsShop's RedeemPoints: access_token/defid/expected_points_cost are POST
	// form fields (not query-string), session: ESession.None since access_token in the body is the auth, the same
	// pattern already used by RandomAvatarFrame/RandomProfileBackground's Set* calls.
	private static async Task<bool> RedeemPointsAsync(Bot bot, string token, uint defID, long expectedPointsCost) {
		Uri request = new(SteamApiURL, "/ILoyaltyRewardsService/RedeemPoints/v1/");

		Dictionary<string, string> data = new(3, StringComparer.Ordinal) {
			{ "access_token", token },
			{ "defid", defID.ToString(CultureInfo.InvariantCulture) },
			{ "expected_points_cost", expectedPointsCost.ToString(CultureInfo.InvariantCulture) },
		};

		return await bot.ArchiWebHandler.UrlPostWithSession(request, data: data, referer: PointsShopReferer, session: ArchiWebHandler.ESession.None).ConfigureAwait(false);
	}

	private sealed record RewardItemsEnvelope([property: JsonPropertyName("response")] RewardItemsData? Response);

	private sealed record RewardItemsData(
		[property: JsonPropertyName("definitions")] List<RewardItemRecord>? Definitions,
		[property: JsonPropertyName("next_cursor")] string? NextCursor
	);

	private sealed record RewardItemRecord(
		[property: JsonPropertyName("appid")] uint? AppID,
		[property: JsonPropertyName("defid")] uint? DefID,
		[property: JsonPropertyName("type")] int? Type,
		[property: JsonPropertyName("community_item_class")] int? CommunityItemClass,
		[property: JsonPropertyName("community_item_type")] int? CommunityItemType,
		[property: JsonPropertyName("point_cost")] string? PointCost,
		[property: JsonPropertyName("active")] bool? Active
	);

	private sealed record CommunityInventoryEnvelope([property: JsonPropertyName("response")] CommunityInventoryData? Response);

	private sealed record CommunityInventoryData([property: JsonPropertyName("items")] List<CommunityInventoryItem>? Items);

	private sealed record CommunityInventoryItem(
		[property: JsonPropertyName("appid")] uint? AppID,
		[property: JsonPropertyName("item_type")] int? ItemType
	);
}
#pragma warning restore CA5394
#pragma warning restore CA1001
#pragma warning restore CA1812
