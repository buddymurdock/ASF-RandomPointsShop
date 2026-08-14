# ASF-RandomPointsShop

Плагин для **[ArchiSteamFarm](https://github.com/JustArchiNET/ArchiSteamFarm)**, который через случайные интервалы тратит накопленные у бота Steam Points на случайный подходящий по цене товар из Points Shop (фон профиля, эмоции, мини-фон, рамка аватара) — вместо того, чтобы очки просто копились без дела.

Использует те же эндпоинты, что и сама страница `store.steampowered.com/points/shop` — `ILoyaltyRewardsService/QueryRewardItems` (публичный каталог), `IQuestService/GetCommunityInventory` (что уже есть у бота — чтобы не покупать дубликат) и `ILoyaltyRewardsService/RedeemPoints` (собственно трата) — тот же набор, что использует рабочий плагин [DevSplash/FreePointsShop](https://github.com/DevSplash/FreePointsShop) для бесплатных предметов, здесь применён к платным.

Пауза между попытками задаётся диапазоном `[MinDelayHours; MaxDelayHours]` часов, но это **не жёсткие границы** — задержка берётся из клэмпированного лог-нормального распределения (min/max ≈ 5-й/95-й перцентиль, медиана `sqrt(min*max)`), а не uniform.

## Как выбирается предмет

1. Общий на все боты каталог (`QueryRewardItems`, кэш `CatalogCacheHours` часов) фильтруется по `AllowedItemClasses`, активности (`active`), исключая наборы (`type == 6`, у них своя, не реализованная здесь логика вложенных `defid`) и предметы без цены в очках.
2. Для конкретного бота через `GetCommunityInventory` вычитаются уже принадлежащие предметы (сравнение по `appid`+`community_item_type`).
3. Из оставшихся кандидатов берутся `CheapestCandidateCount` самых дешёвых, и один — случайно.
4. Плагин **не проверяет баланс очков заранее** (у `GetSummary` нет проверенной эталонной реализации) — вместо этого он смещает выбор к дешёвым предметам и просто трактует неудачный `RedeemPoints` (не хватило очков, предмет уже не активен) как обычный исход, без лишнего шума, повтор — на следующем случайном тике.

Плагин работает **бессрочно**, в отличие от [RandomWishlistAdditions](https://github.com/buddymurdock/ASF-RandomWishlistAdditions)/[RandomFollows](https://github.com/buddymurdock/ASF-RandomFollows) — очки продолжают копиться от дропа карт/крафта бейджей, так что цели "N трат и хватит" здесь нет.

## Установка

1. Скачайте архив плагина из [Releases](../../releases) и распакуйте в папку `plugins` рядом с ASF (создайте подпапку с именем плагина).
2. Перезапустите ASF.

## Конфигурация

Настройки задаются **глобально**, в `ASF.json`, как дополнительные (нераспознанные ASF) свойства верхнего уровня:

```json
{
	"RandomPointsShopEnabled": true,
	"RandomPointsShopMinDelayHours": 24,
	"RandomPointsShopMaxDelayHours": 168,
	"RandomPointsShopCatalogCacheHours": 6,
	"RandomPointsShopCheapestCandidateCount": 10,
	"RandomPointsShopAllowedItemClasses": [3, 4, 13, 14]
}
```

| Свойство | Тип | По умолчанию | Описание |
| --- | --- | --- | --- |
| `RandomPointsShopEnabled` | `bool` | `false` | Включает/выключает плагин. |
| `RandomPointsShopMinDelayHours` | `ushort` | `24` | Нижняя граница (≈5-й перцентиль) случайной паузы между попытками, в часах. |
| `RandomPointsShopMaxDelayHours` | `ushort` | `168` | Верхняя граница (≈95-й перцентиль) случайной паузы. |
| `RandomPointsShopCatalogCacheHours` | `ushort` | `6` | Как часто (в часах) обновлять общий на все боты каталог Points Shop. |
| `RandomPointsShopCheapestCandidateCount` | `byte` | `10` | Сколько самых дешёвых подходящих предметов рассматривать при случайном выборе. |
| `RandomPointsShopAllowedItemClasses` | `int[]` | `[3, 4, 13, 14]` | Разрешённые `community_item_class` (Steam): `3`=фон профиля, `4`=эмоция, `13`=мини-фон, `14`=рамка аватара. Намеренно исключены `1`=бейдж (нет проверенного способа обнаружения владения), `15`=анимированный аватар (конфликтует со слотом [RandomProfileAvatar](https://github.com/buddymurdock/ASF-RandomProfileAvatar)), `16`=скин клавиатуры Steam Deck (не актуально для фарм-бота). |

Если `Min` больше `Max` — значения меняются местами автоматически.

## Сборка

Проект использует **[ASF-PluginTemplate](https://github.com/JustArchiNET/ASF-PluginTemplate)** и собирается вместе с исходниками ASF, подключёнными как git submodule:

```sh
git clone --recurse-submodules https://github.com/buddymurdock/ASF-RandomPointsShop.git
cd ASF-RandomPointsShop
dotnet build -c Release
```

Если репозиторий уже склонирован без `--recurse-submodules`, подтяните submodule отдельно:

```sh
git submodule update --init --recursive
```

## Лицензия

Apache-2.0, см. [LICENSE.txt](LICENSE.txt).
