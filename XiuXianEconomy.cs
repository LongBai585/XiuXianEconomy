using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Timers;
using Newtonsoft.Json;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.Hooks;
using Microsoft.Xna.Framework;
using Timer = System.Timers.Timer;

namespace XiuXianEconomy
{
    [ApiVersion(2, 1)]
    public class XiuXianEconomy : TerrariaPlugin
    {
        #region 基础配置
        private static readonly string ConfigPath = Path.Combine(TShock.SavePath, "XiuXianEconomy.json");
        private static readonly string DataPath = Path.Combine(TShock.SavePath, "XiuXianEconomyData.json");
        private static readonly string AuctionPath = Path.Combine(TShock.SavePath, "XiuXianAuction.json");
        
        private Timer _uiRefreshTimer;
        private StatusManager statusManager;
        
        // 前置插件实例
        private TerrariaPlugin _shouyuanPlugin;
        
        // 前置插件检查
        private bool CheckShouyuanPreReq()
        {
            try
            {
                var plugins = TerrariaApi.Server.ServerApi.Plugins;
                _shouyuanPlugin = plugins.FirstOrDefault(p => p.Plugin.Name == "XiuXianShouYuan" || p.Plugin.Name == "修仙星宿系统")?.Plugin;
                
                if (_shouyuanPlugin == null)
                {
                    TShock.Log.ConsoleError("【严重错误】修仙经济插件需要前置插件 XiuXianShouYuan.dll（修仙星宿系统）！");
                    TShock.Log.ConsoleError("请确保修仙主插件已正确安装并加载，然后重启服务器。");
                    return false;
                }
                
                TShock.Log.ConsoleInfo($"【成功】检测到前置插件: {_shouyuanPlugin.Name} v{_shouyuanPlugin.Version}");
                TShock.Log.ConsoleInfo("修仙经济系统已成功连接到修仙主插件！");
                return true;
            }
            catch (Exception ex)
            {
                TShock.Log.ConsoleError($"检查前置插件时发生错误: {ex.Message}");
                return false;
            }
        }

        public override string Name => "修仙经济系统";
        public override string Author => "泷白";
        public override Version Version => new Version(1, 0, 0);
        public override string Description => "修仙插件经济附属系统，包含星晶货币、拍卖行和商店 - 需要XiuXianShouYuan前置";

        public XiuXianEconomy(Main game) : base(game)
        {
            Order = 2;
            statusManager = new StatusManager();
        }
        #endregion

        #region 星晶货币系统
        public enum StarCrystalGrade
        {
            低等 = 1,    // 绿色
            中等 = 2,    // 蓝色  
            高等 = 3,    // 紫色
            极品 = 4     // 金色
        }

        public class StarCrystal
        {
            public StarCrystalGrade Grade { get; set; }
            public int Amount { get; set; }
            public string ColorHex => Grade switch
            {
                StarCrystalGrade.低等 => "00FF00", // 绿色
                StarCrystalGrade.中等 => "0099FF", // 蓝色
                StarCrystalGrade.高等 => "CC00FF", // 紫色
                StarCrystalGrade.极品 => "FFD700", // 金色
                _ => "FFFFFF"
            };
            
            public string DisplayName => Grade switch
            {
                StarCrystalGrade.低等 => "低等星晶",
                StarCrystalGrade.中等 => "中等星晶", 
                StarCrystalGrade.高等 => "高等星晶",
                StarCrystalGrade.极品 => "极品星晶",
                _ => "未知星晶"
            };
            
            public int ToLowGradeValue()
            {
                return Grade switch
                {
                    StarCrystalGrade.低等 => Amount,
                    StarCrystalGrade.中等 => Amount * 100,
                    StarCrystalGrade.高等 => Amount * 10000,
                    StarCrystalGrade.极品 => Amount * 1000000,
                    _ => Amount
                };
            }
            
            public static StarCrystal FromLowGradeValue(int lowGradeValue)
            {
                var result = new Dictionary<StarCrystalGrade, int>();
                
                if (lowGradeValue >= 1000000)
                {
                    result[StarCrystalGrade.极品] = lowGradeValue / 1000000;
                    lowGradeValue %= 1000000;
                }
                if (lowGradeValue >= 10000)
                {
                    result[StarCrystalGrade.高等] = lowGradeValue / 10000;
                    lowGradeValue %= 10000;
                }
                if (lowGradeValue >= 100)
                {
                    result[StarCrystalGrade.中等] = lowGradeValue / 100;
                    lowGradeValue %= 100;
                }
                if (lowGradeValue > 0)
                {
                    result[StarCrystalGrade.低等] = lowGradeValue;
                }
                
                // 返回主要面额
                if (result.ContainsKey(StarCrystalGrade.极品))
                    return new StarCrystal { Grade = StarCrystalGrade.极品, Amount = result[StarCrystalGrade.极品] };
                else if (result.ContainsKey(StarCrystalGrade.高等))
                    return new StarCrystal { Grade = StarCrystalGrade.高等, Amount = result[StarCrystalGrade.高等] };
                else if (result.ContainsKey(StarCrystalGrade.中等))
                    return new StarCrystal { Grade = StarCrystalGrade.中等, Amount = result[StarCrystalGrade.中等] };
                else
                    return new StarCrystal { Grade = StarCrystalGrade.低等, Amount = result[StarCrystalGrade.低等] };
            }
        }

        public class EconomyData
        {
            public string PlayerName { get; set; }
            public Dictionary<StarCrystalGrade, int> StarCrystals { get; set; } = new Dictionary<StarCrystalGrade, int>();
            public List<AuctionItem> AuctionItems { get; set; } = new List<AuctionItem>();
            public DateTime LastDailyReward { get; set; } = DateTime.MinValue;
            
            public int GetTotalLowGradeValue()
            {
                int total = 0;
                foreach (var kvp in StarCrystals)
                {
                    var crystal = new StarCrystal { Grade = kvp.Key, Amount = kvp.Value };
                    total += crystal.ToLowGradeValue();
                }
                return total;
            }
            
            public void AddStarCrystals(StarCrystalGrade grade, int amount)
            {
                if (!StarCrystals.ContainsKey(grade))
                    StarCrystals[grade] = 0;
                StarCrystals[grade] += amount;
            }
            
            public bool RemoveStarCrystals(StarCrystalGrade grade, int amount)
            {
                if (!StarCrystals.ContainsKey(grade) || StarCrystals[grade] < amount)
                    return false;
                    
                StarCrystals[grade] -= amount;
                if (StarCrystals[grade] == 0)
                    StarCrystals.Remove(grade);
                    
                return true;
            }
            
            public string GetBalanceDisplay()
            {
                if (StarCrystals.Count == 0)
                    return "无星晶";
                    
                var display = new List<string>();
                foreach (var grade in Enum.GetValues(typeof(StarCrystalGrade)).Cast<StarCrystalGrade>().Reverse())
                {
                    if (StarCrystals.ContainsKey(grade) && StarCrystals[grade] > 0)
                    {
                        var crystal = new StarCrystal { Grade = grade, Amount = StarCrystals[grade] };
                        display.Add($"[c/{crystal.ColorHex}:{crystal.Amount}{GetGradeAbbr(grade)}]");
                    }
                }
                return string.Join(" ", display);
            }
        }

        public static Dictionary<string, EconomyData> EconomyPlayers = new Dictionary<string, EconomyData>();
        #endregion

        #region 拍卖行系统
        public class AuctionItem
        {
            public string Id { get; set; } = Guid.NewGuid().ToString();
            public string Seller { get; set; }
            public int ItemId { get; set; }
            public int Stack { get; set; }
            public byte Prefix { get; set; }
            public StarCrystalGrade PriceGrade { get; set; }
            public int PriceAmount { get; set; }
            public DateTime ListTime { get; set; } = DateTime.Now;
            public DateTime ExpireTime { get; set; } = DateTime.Now.AddDays(7);
            public string Buyer { get; set; }
            public bool IsSold { get; set; }
            public bool IsExpired => DateTime.Now > ExpireTime;
            
            public string GetPriceDisplay()
            {
                var crystal = new StarCrystal { Grade = PriceGrade, Amount = PriceAmount };
                return $"[c/{crystal.ColorHex}:{PriceAmount}{GetGradeAbbr(PriceGrade)}]";
            }
            
            public string GetItemDisplay()
            {
                var itemName = TShock.Utils.GetItemById(ItemId)?.Name ?? $"物品ID:{ItemId}";
                return $"{itemName} x{Stack}";
            }
        }

        public static List<AuctionItem> AuctionHouse = new List<AuctionItem>();
        
        private void InitializeAuction()
        {
            try
            {
                if (File.Exists(AuctionPath))
                {
                    var json = File.ReadAllText(AuctionPath);
                    AuctionHouse = JsonConvert.DeserializeObject<List<AuctionItem>>(json) ?? new List<AuctionItem>();
                    
                    // 清理过期的拍卖品
                    AuctionHouse.RemoveAll(a => a.IsExpired && !a.IsSold);
                    
                    TShock.Log.Info($"拍卖行数据已加载，共有 {AuctionHouse.Count} 个拍卖品");
                }
                else
                {
                    TShock.Log.Info("未找到拍卖行数据文件，将创建新的拍卖行");
                }
            }
            catch (Exception ex)
            {
                TShock.Log.Error($"加载拍卖行数据失败: {ex.Message}");
                AuctionHouse = new List<AuctionItem>();
            }
        }
        
        private void SaveAuction()
        {
            try
            {
                File.WriteAllText(AuctionPath, JsonConvert.SerializeObject(AuctionHouse, Formatting.Indented));
                TShock.Log.Info($"拍卖行数据已保存，共有 {AuctionHouse.Count} 个拍卖品");
            }
            catch (Exception ex)
            {
                TShock.Log.Error($"保存拍卖行数据失败: {ex.Message}");
            }
        }
        #endregion

        #region 商店系统
        public class ShopItem
        {
            [JsonProperty("物品ID")]
            public int ItemId { get; set; }
            
            [JsonProperty("库存")]
            public int Stock { get; set; } = -1; // -1 表示无限
            
            [JsonProperty("价格品质")]
            public StarCrystalGrade PriceGrade { get; set; }
            
            [JsonProperty("价格数量")]
            public int PriceAmount { get; set; }
            
            [JsonProperty("购买限制")]
            public int PurchaseLimit { get; set; } = -1; // -1 表示无限制
            
            [JsonProperty("玩家购买记录")]
            public Dictionary<string, int> PlayerPurchases { get; set; } = new Dictionary<string, int>();
            
            public string GetPriceDisplay()
            {
                var crystal = new StarCrystal { Grade = PriceGrade, Amount = PriceAmount };
                return $"[c/{crystal.ColorHex}:{PriceAmount}{GetGradeAbbr(PriceGrade)}]";
            }
            
            public string GetItemDisplay()
            {
                var itemName = TShock.Utils.GetItemById(ItemId)?.Name ?? $"物品ID:{ItemId}";
                return $"{itemName}";
            }
            
            public bool CanPlayerPurchase(string playerName, int quantity = 1)
            {
                if (PurchaseLimit == -1) return true;
                
                if (!PlayerPurchases.ContainsKey(playerName))
                    PlayerPurchases[playerName] = 0;
                    
                return PlayerPurchases[playerName] + quantity <= PurchaseLimit;
            }
            
            public void RecordPurchase(string playerName, int quantity = 1)
            {
                if (PurchaseLimit == -1) return;
                
                if (!PlayerPurchases.ContainsKey(playerName))
                    PlayerPurchases[playerName] = 0;
                    
                PlayerPurchases[playerName] += quantity;
            }
        }

        public class EconomyConfig
        {
            [JsonProperty("商店物品")]
            public List<ShopItem> ShopItems { get; set; } = new List<ShopItem>();
            
            [JsonProperty("低等掉落概率")]
            public double DropChanceLow { get; set; } = 0.3;
            
            [JsonProperty("中等掉落概率")]
            public double DropChanceMedium { get; set; } = 0.15;
            
            [JsonProperty("高等掉落概率")]
            public double DropChanceHigh { get; set; } = 0.05;
            
            [JsonProperty("极品掉落概率")]
            public double DropChanceExtreme { get; set; } = 0.01;
            
            [JsonProperty("每日奖励低等")]
            public int DailyRewardLow { get; set; } = 10;
            
            [JsonProperty("每日奖励中等")]
            public int DailyRewardMedium { get; set; } = 5;
            
            [JsonProperty("每日奖励高等")]
            public int DailyRewardHigh { get; set; } = 2;
            
            [JsonProperty("每日奖励极品")]
            public int DailyRewardExtreme { get; set; } = 1;
            
            [JsonProperty("顶部ui偏移X")]
            public int TopuiOffsetX { get; set; } = -13;
            
            [JsonProperty("顶部ui偏移Y")]
            public int TopuiOffsetY { get; set; } = 12;
            
            [JsonProperty("聊天ui偏移X")]
            public int ChatuiOffsetX { get; set; } = 2;
            
            [JsonProperty("聊天ui偏移Y")]
            public int ChatuiOffsetY { get; set; } = 1;

            [JsonProperty("经济系统启用")]
            public bool EconomyEnabled { get; set; } = true;

            [JsonProperty("自动给予初始星晶")]
            public bool GiveInitialCrystals { get; set; } = true;

            [JsonProperty("初始星晶数量")]
            public int InitialCrystals { get; set; } = 100;

            [JsonProperty("ui刷新频率毫秒")]
            public int uiRefreshInterval { get; set; } = 10000; // 默认10秒
            
            public static EconomyConfig Instance;
            
            public static void Load(string path)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        Instance = JsonConvert.DeserializeObject<EconomyConfig>(File.ReadAllText(path));
                        TShock.Log.Info($"经济配置已加载，商店物品: {Instance.ShopItems?.Count ?? 0} 个");
                    }
                    else
                    {
                        Instance = new EconomyConfig();
                        InitializeDefaultShop();
                        Save(path);
                        TShock.Log.Info("创建了默认经济配置文件");
                    }
                }
                catch (Exception ex)
                {
                    TShock.Log.Error($"加载经济配置失败: {ex.Message}");
                    Instance = new EconomyConfig();
                }
            }
            
            public static void Save(string path)
            {
                try
                {
                    File.WriteAllText(path, JsonConvert.SerializeObject(Instance, Formatting.Indented));
                    TShock.Log.Info("经济配置已保存");
                }
                catch (Exception ex)
                {
                    TShock.Log.Error($"保存经济配置失败: {ex.Message}");
                }
            }
            
            private static void InitializeDefaultShop()
            {
                // 添加一些默认商店物品
                Instance.ShopItems.AddRange(new[]
                {
                    new ShopItem { ItemId = 1, Stock = -1, PriceGrade = StarCrystalGrade.低等, PriceAmount = 10 }, // 铁锤
                    new ShopItem { ItemId = 2, Stock = -1, PriceGrade = StarCrystalGrade.低等, PriceAmount = 5 },  // 蘑菇
                    new ShopItem { ItemId = 3, Stock = -1, PriceGrade = StarCrystalGrade.中等, PriceAmount = 3 },  // 恢复药水
                    new ShopItem { ItemId = 4, Stock = 10, PriceGrade = StarCrystalGrade.高等, PriceAmount = 1, PurchaseLimit = 1 }, // 铜短剑，限量
                });
            }
        }
        #endregion

        #region 数据管理
        public static EconomyData GetPlayerEconomy(string name)
        {
            if (!EconomyPlayers.TryGetValue(name, out EconomyData data))
            {
                data = new EconomyData { PlayerName = name };
                
                // 如果配置允许，给予初始星晶
                if (EconomyConfig.Instance.GiveInitialCrystals)
                {
                    data.StarCrystals[StarCrystalGrade.低等] = EconomyConfig.Instance.InitialCrystals;
                }
                
                EconomyPlayers[name] = data;
            }
            return data;
        }

        public static void LoadEconomy(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    EconomyPlayers = JsonConvert.DeserializeObject<Dictionary<string, EconomyData>>(File.ReadAllText(path))
                                   ?? new Dictionary<string, EconomyData>();
                    TShock.Log.Info($"经济数据已加载，共有 {EconomyPlayers.Count} 名玩家的经济数据");
                }
                else
                {
                    TShock.Log.Info("未找到经济数据文件，将创建新的经济数据");
                }
            }
            catch (Exception ex)
            {
                TShock.Log.Error($"加载经济数据失败: {ex.Message}");
            }
        }

        public static void SaveEconomy(string path)
        {
            try
            {
                File.WriteAllText(path, JsonConvert.SerializeObject(EconomyPlayers, Formatting.Indented));
                TShock.Log.Info($"经济数据已保存，共有 {EconomyPlayers.Count} 名玩家的经济数据");
            }
            catch (Exception ex)
            {
                TShock.Log.Error($"保存经济数据失败: {ex.Message}");
            }
        }

        public static void SavePlayerEconomy(string name)
        {
            try
            {
                if (EconomyPlayers.ContainsKey(name))
                    SaveEconomy(DataPath);
            }
            catch (Exception ex)
            {
                TShock.Log.Error($"保存玩家 {name} 经济数据失败: {ex.Message}");
            }
        }
        #endregion

        #region 怪物掉落系统
        private void OnNpcKilled(NpcKilledEventArgs args)
        {
            try
            {
                // 检查经济系统是否启用
                if (!EconomyConfig.Instance.EconomyEnabled)
                    return;

                NPC npc = args.npc;
                var players = TShock.Players.Where(p => p != null && p.Active && p.IsLoggedIn).ToList();
                
                if (players.Count == 0) return;
                
                // 随机选择一个玩家获得掉落（模拟团队掉落）
                var luckyPlayer = players[new Random().Next(players.Count)];
                ProcessMonsterDrop(luckyPlayer, npc);
            }
            catch (Exception ex)
            {
                TShock.Log.Error($"处理怪物掉落失败: {ex.Message}");
            }
        }
        
        private void ProcessMonsterDrop(TSPlayer player, NPC npc)
        {
            var random = new Random();
            var config = EconomyConfig.Instance;
            
            // 根据NPC的强度决定掉落品质
            double powerFactor = npc.lifeMax / 100.0;
            powerFactor = Math.Min(powerFactor, 10.0); // 限制最大系数
            
            // 计算掉落几率
            double lowChance = config.DropChanceLow * powerFactor;
            double mediumChance = config.DropChanceMedium * powerFactor;
            double highChance = config.DropChanceHigh * powerFactor;
            double extremeChance = config.DropChanceExtreme * powerFactor;
            
            StarCrystalGrade? dropGrade = null;
            int dropAmount = 1;
            
            double roll = random.NextDouble();
            
            if (roll < extremeChance)
            {
                dropGrade = StarCrystalGrade.极品;
                dropAmount = random.Next(1, 3);
            }
            else if (roll < highChance)
            {
                dropGrade = StarCrystalGrade.高等;
                dropAmount = random.Next(1, 5);
            }
            else if (roll < mediumChance)
            {
                dropGrade = StarCrystalGrade.中等;
                dropAmount = random.Next(1, 10);
            }
            else if (roll < lowChance)
            {
                dropGrade = StarCrystalGrade.低等;
                dropAmount = random.Next(1, 20);
            }
            
            if (dropGrade.HasValue)
            {
                var data = GetPlayerEconomy(player.Name);
                data.AddStarCrystals(dropGrade.Value, dropAmount);
                
                var crystal = new StarCrystal { Grade = dropGrade.Value, Amount = dropAmount };
                player.SendMessage($"★★★ 击败 {npc.GivenOrTypeName} 获得 [c/{crystal.ColorHex}:{dropAmount}{GetGradeAbbr(dropGrade.Value)}星晶]！ ★★★", Color.Yellow);
                
                UpdateEconomyui(player, data);
                SavePlayerEconomy(player.Name);
            }
        }
        
        private static string GetGradeAbbr(StarCrystalGrade grade)
        {
            return grade switch
            {
                StarCrystalGrade.低等 => "低",
                StarCrystalGrade.中等 => "中", 
                StarCrystalGrade.高等 => "高",
                StarCrystalGrade.极品 => "极",
                _ => "?"
            };
        }
        #endregion

        #region ui系统
        private void InitializeuiTimer()
        {
            if (_uiRefreshTimer != null)
            {
                _uiRefreshTimer.Stop();
                _uiRefreshTimer.Dispose();
            }
            
            _uiRefreshTimer = new Timer(EconomyConfig.Instance.uiRefreshInterval);
            _uiRefreshTimer.AutoReset = true;
            _uiRefreshTimer.Elapsed += RefreshuiForAllPlayers;
            _uiRefreshTimer.Start();
            
            TShock.Log.Info($"ui刷新定时器已初始化，刷新间隔: {EconomyConfig.Instance.uiRefreshInterval} 毫秒");
        }
        
        private void UpdateEconomyui(TSPlayer player, EconomyData data)
        {
            UpdateChatui(player, data);
            UpdateTopui(player, data);
        }
        
        private void UpdateChatui(TSPlayer player, EconomyData data)
        {
            var sb = new System.Text.StringBuilder();
            var config = EconomyConfig.Instance;
            
            int offsetX = config.ChatuiOffsetX;
            int offsetY = config.ChatuiOffsetY;
            
            if (offsetY > 0)
            {
                sb.Append(new string('\n', offsetY));
            }
            
            string xOffset = (offsetX > 0) ? new string(' ', offsetX) : "";
            
            sb.AppendLine($"{xOffset}{"星晶余额:".Color(Color.LightGreen)} {data.GetBalanceDisplay()}");
            
            // 显示今日可领取奖励
            if (data.LastDailyReward.Date < DateTime.Today)
            {
                sb.AppendLine($"{xOffset}{"每日奖励: [c/00FF00:可领取]".Color(Color.Yellow)}");
            }
            
            // 显示拍卖行信息
            int activeAuctions = AuctionHouse.Count(a => !a.IsSold && !a.IsExpired);
            if (activeAuctions > 0)
            {
                sb.AppendLine($"{xOffset}{$"拍卖行: {activeAuctions}个物品".Color(Color.Orange)}");
            }
            
            player.SendMessage(sb.ToString(), Color.White);
        }
        
        private void UpdateTopui(TSPlayer player, EconomyData data)
        {
            var sb = new System.Text.StringBuilder();
            var config = EconomyConfig.Instance;

            if (config.TopuiOffsetY > 0)
            {
                sb.Append(new string('\n', config.TopuiOffsetY));
            }

            int absXOffset = Math.Abs(config.TopuiOffsetX);
            string xOffset = new string(' ', absXOffset);

            Func<string, string> applyXOffset = line =>
            {
                if (config.TopuiOffsetX < 0)
                    return line + xOffset;
                else
                    return xOffset + line;
            };

            // 使用与修仙插件一致的顶部ui风格
            sb.AppendLine(applyXOffset($"星晶货币: {data.GetBalanceDisplay()}".Color(Color.LightSkyBlue)));
            
            // 显示财富等级
            int totalValue = data.GetTotalLowGradeValue();
            string wealthLevel = totalValue switch
            {
                > 1000000 => "[c/FFD700:富可敌国]",
                > 100000 => "[c/CC00FF:家财万贯]", 
                > 10000 => "[c/0099FF:小有资产]",
                > 1000 => "[c/00FF00:温饱有余]",
                _ => "[c/AAAAAA:一贫如洗]"
            };
            
            sb.AppendLine(applyXOffset($"财富等级: {wealthLevel}".Color(Color.LightGreen)));

            // 显示拍卖信息
            int activeAuctions = AuctionHouse.Count(a => !a.IsSold && !a.IsExpired);
            if (activeAuctions > 0)
            {
                sb.AppendLine(applyXOffset($"拍卖行: {activeAuctions}个物品".Color(Color.Orange)));
            }

            // 显示每日奖励状态
            if (data.LastDailyReward.Date < DateTime.Today)
            {
                sb.AppendLine(applyXOffset("每日奖励: [c/00FF00:可领取]".Color(Color.Yellow)));
            }

            statusManager.AddOrUpdateText(player, "top_economy_info", sb.ToString().TrimEnd());
        }
        
        private void RefreshuiForAllPlayers(object sender, ElapsedEventArgs e)
        {
            try
            {
                // 检查经济系统是否启用
                if (!EconomyConfig.Instance.EconomyEnabled)
                    return;

                foreach (var player in TShock.Players.Where(p => p != null && p.Active && p.IsLoggedIn))
                {
                    try
                    {
                        var data = GetPlayerEconomy(player.Name);
                        UpdateEconomyui(player, data);
                    }
                    catch (Exception ex)
                    {
                        TShock.Log.Error($"刷新 {player.Name} 的经济ui失败: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                TShock.Log.Error($"刷新经济ui失败: {ex.Message}");
            }
        }
        #endregion

        #region 命令系统
        private void InitializeCommands()
        {
            Commands.ChatCommands.Add(new Command("economy.player", CheckBalance, "星晶余额"));
            Commands.ChatCommands.Add(new Command("economy.player", DailyReward, "每日奖励"));
            Commands.ChatCommands.Add(new Command("economy.player", OpenShop, "星晶商店"));
            Commands.ChatCommands.Add(new Command("economy.player", OpenAuction, "拍卖行"));
            Commands.ChatCommands.Add(new Command("economy.player", ListAuction, "上架拍卖"));
            Commands.ChatCommands.Add(new Command("economy.player", BuyAuction, "购买拍卖"));
            Commands.ChatCommands.Add(new Command("economy.player", BuyShop, "购买"));
            Commands.ChatCommands.Add(new Command("economy.admin", AdminAddCrystals, "添加星晶"));
            Commands.ChatCommands.Add(new Command("economy.admin", AdminReloadEconomy, "重读经济"));
            Commands.ChatCommands.Add(new Command("economy.admin", ToggleEconomy, "开关经济系统"));
            Commands.ChatCommands.Add(new Command("economy.admin", SetuiRefreshRate, "设置ui刷新频率"));
            Commands.ChatCommands.Add(new Command("economy.player", CheckuiRefreshRate, "查看ui刷新频率"));
            
            // 创建默认权限组
            CreateDefaultPermissions();
        }
        
        private void CreateDefaultPermissions()
        {
            try
            {
                // 为修仙弟子组添加经济权限
                var xiuxianGroup = TShock.Groups.GetGroupByName("修仙弟子");
                if (xiuxianGroup != null && !xiuxianGroup.HasPermission("economy.player"))
                {
                    TShock.Groups.AddPermissions("修仙弟子", new List<string> { "economy.player" });
                    TShock.Log.Info("已为修仙弟子组添加经济权限");
                }

                // 为修仙仙尊组添加管理员权限
                var adminGroup = TShock.Groups.GetGroupByName("修仙仙尊");
                if (adminGroup != null && !adminGroup.HasPermission("economy.admin"))
                {
                    TShock.Groups.AddPermissions("修仙仙尊", new List<string> { "economy.player", "economy.admin" });
                    TShock.Log.Info("已为修仙仙尊组添加经济管理员权限");
                }
            }
            catch (Exception ex)
            {
                TShock.Log.Error($"创建经济权限组失败: {ex.Message}");
            }
        }
        
        private void CheckBalance(CommandArgs args)
        {
            if (!EconomyConfig.Instance.EconomyEnabled)
            {
                args.Player.SendErrorMessage("经济系统当前已关闭");
                return;
            }

            var data = GetPlayerEconomy(args.Player.Name);
            args.Player.SendMessage("══════════ 星晶余额 ══════════", Color.Cyan);
            args.Player.SendMessage($"总价值: {data.GetTotalLowGradeValue()} 低等星晶", Color.White);
            args.Player.SendMessage($"详细余额: {data.GetBalanceDisplay()}", Color.White);
            
            // 显示财富等级
            int totalValue = data.GetTotalLowGradeValue();
            string wealthLevel = totalValue switch
            {
                > 1000000 => "[c/FFD700:富可敌国]",
                > 100000 => "[c/CC00FF:家财万贯]", 
                > 10000 => "[c/0099FF:小有资产]",
                > 1000 => "[c/00FF00:温饱有余]",
                _ => "[c/AAAAAA:一贫如洗]"
            };
            args.Player.SendMessage($"财富等级: {wealthLevel}", Color.White);
            
            args.Player.SendMessage("══════════════════════════════", Color.Cyan);
        }
        
        private void DailyReward(CommandArgs args)
        {
            if (!EconomyConfig.Instance.EconomyEnabled)
            {
                args.Player.SendErrorMessage("经济系统当前已关闭");
                return;
            }

            var data = GetPlayerEconomy(args.Player.Name);
            var config = EconomyConfig.Instance;
            
            if (data.LastDailyReward.Date >= DateTime.Today)
            {
                args.Player.SendErrorMessage("今日已领取过每日奖励！");
                return;
            }
            
            data.LastDailyReward = DateTime.Now;
            data.AddStarCrystals(StarCrystalGrade.低等, config.DailyRewardLow);
            data.AddStarCrystals(StarCrystalGrade.中等, config.DailyRewardMedium);
            data.AddStarCrystals(StarCrystalGrade.高等, config.DailyRewardHigh);
            data.AddStarCrystals(StarCrystalGrade.极品, config.DailyRewardExtreme);
            
            args.Player.SendSuccessMessage("每日奖励领取成功！");
            args.Player.SendMessage($"获得: {config.DailyRewardLow}低等星晶, {config.DailyRewardMedium}中等星晶, {config.DailyRewardHigh}高等星晶, {config.DailyRewardExtreme}极品星晶", Color.Yellow);
            
            UpdateEconomyui(args.Player, data);
            SavePlayerEconomy(args.Player.Name);
        }
        
        private void OpenShop(CommandArgs args)
        {
            if (!EconomyConfig.Instance.EconomyEnabled)
            {
                args.Player.SendErrorMessage("经济系统当前已关闭");
                return;
            }

            var shopItems = EconomyConfig.Instance.ShopItems;
            if (shopItems.Count == 0)
            {
                args.Player.SendErrorMessage("商店暂无商品！");
                return;
            }
            
            args.Player.SendMessage("══════════ 星晶商店 ══════════", Color.Gold);
            for (int i = 0; i < shopItems.Count; i++)
            {
                var item = shopItems[i];
                var itemName = TShock.Utils.GetItemById(item.ItemId)?.Name ?? $"物品ID:{item.ItemId}";
                string stockInfo = item.Stock == -1 ? "无限" : $"剩余:{item.Stock}";
                string limitInfo = item.PurchaseLimit == -1 ? "" : $", 限购:{item.PurchaseLimit}";
                
                args.Player.SendMessage($"{i + 1}. {itemName} - {item.GetPriceDisplay()} ({stockInfo}{limitInfo})", Color.White);
            }
            args.Player.SendMessage("使用 /购买 <编号> [数量] 购买商品", Color.Yellow);
            args.Player.SendMessage("══════════════════════════════", Color.Gold);
        }
        
        private void BuyShop(CommandArgs args)
        {
            if (!EconomyConfig.Instance.EconomyEnabled)
            {
                args.Player.SendErrorMessage("经济系统当前已关闭");
                return;
            }

            if (args.Parameters.Count < 1)
            {
                args.Player.SendErrorMessage("用法: /购买 <编号> [数量]");
                return;
            }
            
            if (!int.TryParse(args.Parameters[0], out int index) || index < 1)
            {
                args.Player.SendErrorMessage("无效的商品编号！");
                return;
            }
            
            int quantity = 1;
            if (args.Parameters.Count > 1 && (!int.TryParse(args.Parameters[1], out quantity) || quantity < 1))
            {
                args.Player.SendErrorMessage("无效的数量！");
                return;
            }
            
            var shopItems = EconomyConfig.Instance.ShopItems;
            if (index > shopItems.Count)
            {
                args.Player.SendErrorMessage("商品编号不存在！");
                return;
            }
            
            var item = shopItems[index - 1];
            var data = GetPlayerEconomy(args.Player.Name);
            
            // 检查库存
            if (item.Stock != -1 && item.Stock < quantity)
            {
                args.Player.SendErrorMessage($"商品库存不足！剩余: {item.Stock}");
                return;
            }
            
            // 检查购买限制
            if (!item.CanPlayerPurchase(args.Player.Name, quantity))
            {
                args.Player.SendErrorMessage($"已达到购买限制！限购: {item.PurchaseLimit}");
                return;
            }
            
            // 检查余额
            int totalCost = item.PriceAmount * quantity;
            if (!data.RemoveStarCrystals(item.PriceGrade, totalCost))
            {
                args.Player.SendErrorMessage($"星晶不足！需要: {item.GetPriceDisplay()} x{quantity}");
                return;
            }
            
            // 执行购买
            if (item.Stock != -1)
                item.Stock -= quantity;
                
            item.RecordPurchase(args.Player.Name, quantity);
            
            // 给予物品
            args.Player.GiveItem(item.ItemId, quantity, 0);
            
            args.Player.SendSuccessMessage($"成功购买 {item.GetItemDisplay()} x{quantity}！");
            args.Player.SendMessage($"消耗: {item.GetPriceDisplay()} x{quantity}", Color.Yellow);
            
            UpdateEconomyui(args.Player, data);
            SavePlayerEconomy(args.Player.Name);
            EconomyConfig.Save(ConfigPath);
        }
        
        private void OpenAuction(CommandArgs args)
        {
            if (!EconomyConfig.Instance.EconomyEnabled)
            {
                args.Player.SendErrorMessage("经济系统当前已关闭");
                return;
            }

            var activeAuctions = AuctionHouse.Where(a => !a.IsSold && !a.IsExpired).ToList();
            if (activeAuctions.Count == 0)
            {
                args.Player.SendErrorMessage("拍卖行暂无商品！");
                return;
            }
            
            args.Player.SendMessage("══════════ 拍卖行 ══════════", Color.Orange);
            for (int i = 0; i < Math.Min(activeAuctions.Count, 10); i++) // 只显示前10个
            {
                var auction = activeAuctions[i];
                string timeLeft = (auction.ExpireTime - DateTime.Now).ToString(@"dd\:hh\:mm");
                args.Player.SendMessage($"{i + 1}. {auction.GetItemDisplay()} - {auction.GetPriceDisplay()} (卖家:{auction.Seller}, 剩余:{timeLeft})", Color.White);
            }
            args.Player.SendMessage("使用 /购买拍卖 <编号> 购买物品", Color.Yellow);
            args.Player.SendMessage("══════════════════════════════", Color.Orange);
        }
        
        private void ListAuction(CommandArgs args)
        {
            if (!EconomyConfig.Instance.EconomyEnabled)
            {
                args.Player.SendErrorMessage("经济系统当前已关闭");
                return;
            }

            if (args.Parameters.Count < 2)
            {
                args.Player.SendErrorMessage("用法: /上架拍卖 <价格> <品质>");
                args.Player.SendErrorMessage("品质: 低等, 中等, 高等, 极品");
                return;
            }
            
            if (!int.TryParse(args.Parameters[0], out int price) || price < 1)
            {
                args.Player.SendErrorMessage("无效的价格！必须是正整数");
                return;
            }
            
            string gradeStr = args.Parameters[1].ToLower();
            StarCrystalGrade grade = StarCrystalGrade.低等;
            
            switch (gradeStr)
            {
                case "低等": grade = StarCrystalGrade.低等; break;
                case "中等": grade = StarCrystalGrade.中等; break;
                case "高等": grade = StarCrystalGrade.高等; break;
                case "极品": grade = StarCrystalGrade.极品; break;
                default:
                    args.Player.SendErrorMessage("无效的品质！可用: 低等, 中等, 高等, 极品");
                    return;
            }
            
            // 检查玩家手中是否有物品
            var playerItem = args.Player.TPlayer.inventory[args.Player.TPlayer.selectedItem];
            if (playerItem.type == 0 || playerItem.stack < 1)
            {
                args.Player.SendErrorMessage("请手持要拍卖的物品！");
                return;
            }
            
            // 创建拍卖品
            var auction = new AuctionItem
            {
                Seller = args.Player.Name,
                ItemId = playerItem.type,
                Stack = playerItem.stack,
                Prefix = playerItem.prefix,
                PriceGrade = grade,
                PriceAmount = price
            };
            
            // 从玩家手中移除物品
            playerItem.SetDefaults(0);
            args.Player.SendData(PacketTypes.PlayerSlot, "", args.Player.Index, args.Player.TPlayer.selectedItem);
            
            // 添加到拍卖行
            AuctionHouse.Add(auction);
            SaveAuction();
            
            args.Player.SendSuccessMessage("物品上架成功！");
            args.Player.SendMessage($"上架: {auction.GetItemDisplay()} - {auction.GetPriceDisplay()}", Color.Yellow);
        }
        
        private void BuyAuction(CommandArgs args)
        {
            if (!EconomyConfig.Instance.EconomyEnabled)
            {
                args.Player.SendErrorMessage("经济系统当前已关闭");
                return;
            }

            if (args.Parameters.Count < 1)
            {
                args.Player.SendErrorMessage("用法: /购买拍卖 <编号>");
                return;
            }
            
            if (!int.TryParse(args.Parameters[0], out int index) || index < 1)
            {
                args.Player.SendErrorMessage("无效的拍卖编号！");
                return;
            }
            
            var activeAuctions = AuctionHouse.Where(a => !a.IsSold && !a.IsExpired).ToList();
            if (index > activeAuctions.Count)
            {
                args.Player.SendErrorMessage("拍卖编号不存在！");
                return;
            }
            
            var auction = activeAuctions[index - 1];
            var data = GetPlayerEconomy(args.Player.Name);
            
            // 检查是否是自己的拍卖
            if (auction.Seller == args.Player.Name)
            {
                args.Player.SendErrorMessage("不能购买自己的拍卖物品！");
                return;
            }
            
            // 检查余额
            if (!data.RemoveStarCrystals(auction.PriceGrade, auction.PriceAmount))
            {
                args.Player.SendErrorMessage($"星晶不足！需要: {auction.GetPriceDisplay()}");
                return;
            }
            
            // 执行交易
            auction.IsSold = true;
            auction.Buyer = args.Player.Name;
            
            // 给予买家物品
            args.Player.GiveItem(auction.ItemId, auction.Stack, auction.Prefix);
            
            // 给予卖家星晶
            var sellerData = GetPlayerEconomy(auction.Seller);
            sellerData.AddStarCrystals(auction.PriceGrade, auction.PriceAmount);
            
            args.Player.SendSuccessMessage($"成功购买 {auction.GetItemDisplay()}！");
            
            // 通知卖家
            var seller = TShock.Players.FirstOrDefault(p => p?.Name == auction.Seller);
            if (seller != null)
            {
                seller.SendSuccessMessage($"你的 {auction.GetItemDisplay()} 已售出！");
                seller.SendMessage($"获得: {auction.GetPriceDisplay()}", Color.Yellow);
                UpdateEconomyui(seller, sellerData);
            }
            
            UpdateEconomyui(args.Player, data);
            SavePlayerEconomy(args.Player.Name);
            SavePlayerEconomy(auction.Seller);
            SaveAuction();
        }
        
        private void AdminAddCrystals(CommandArgs args)
        {
            if (args.Parameters.Count < 3)
            {
                args.Player.SendErrorMessage("用法: /添加星晶 <玩家> <品质> <数量>");
                args.Player.SendErrorMessage("品质: 低等, 中等, 高等, 极品");
                return;
            }
            
            string targetName = args.Parameters[0];
            string gradeStr = args.Parameters[1].ToLower();
            
            if (!int.TryParse(args.Parameters[2], out int amount) || amount < 1)
            {
                args.Player.SendErrorMessage("无效的数量！必须是正整数");
                return;
            }
            
            StarCrystalGrade grade = StarCrystalGrade.低等;
            switch (gradeStr)
            {
                case "低等": grade = StarCrystalGrade.低等; break;
                case "中等": grade = StarCrystalGrade.中等; break;
                case "高等": grade = StarCrystalGrade.高等; break;
                case "极品": grade = StarCrystalGrade.极品; break;
                default:
                    args.Player.SendErrorMessage("无效的品质！可用: 低等, 中等, 高等, 极品");
                    return;
            }
            
            var targetData = GetPlayerEconomy(targetName);
            targetData.AddStarCrystals(grade, amount);
            
            args.Player.SendSuccessMessage($"已为 {targetName} 添加 {amount} {grade}星晶");
            
            var targetPlayer = TShock.Players.FirstOrDefault(p => p?.Name == targetName);
            if (targetPlayer != null)
            {
                targetPlayer.SendSuccessMessage($"管理员 {args.Player.Name} 为你添加了 {amount} {grade}星晶");
                UpdateEconomyui(targetPlayer, targetData);
            }
            
            SavePlayerEconomy(targetName);
        }
        
        private void AdminReloadEconomy(CommandArgs args)
        {
            try
            {
                EconomyConfig.Load(ConfigPath);
                LoadEconomy(DataPath);
                InitializeAuction();
                InitializeuiTimer(); // 重新初始化ui定时器
                
                args.Player.SendSuccessMessage("经济配置重读完成！");
                
                // 更新所有在线玩家的ui
                foreach (var player in TShock.Players.Where(p => p != null && p.Active))
                {
                    var data = GetPlayerEconomy(player.Name);
                    UpdateEconomyui(player, data);
                }
            }
            catch (Exception ex)
            {
                args.Player.SendErrorMessage($"重读经济配置失败: {ex.Message}");
            }
        }

        private void ToggleEconomy(CommandArgs args)
        {
            EconomyConfig.Instance.EconomyEnabled = !EconomyConfig.Instance.EconomyEnabled;
            EconomyConfig.Save(ConfigPath);
            
            string status = EconomyConfig.Instance.EconomyEnabled ? "开启" : "关闭";
            args.Player.SendSuccessMessage($"经济系统已{status}");
            
            // 只向在线的玩家发送消息，避免在初始化时发送
            var onlinePlayers = TShock.Players.Where(p => p != null && p.Active).ToList();
            if (onlinePlayers.Count > 0)
            {
                if (EconomyConfig.Instance.EconomyEnabled)
                {
                    TSPlayer.All.SendInfoMessage($"经济系统已开启！使用 /星晶余额 查看余额");
                }
                else
                {
                    TSPlayer.All.SendInfoMessage($"经济系统已关闭，所有经济功能暂时不可用");
                }
            }
        }

        private void SetuiRefreshRate(CommandArgs args)
        {
            if (args.Parameters.Count < 1)
            {
                args.Player.SendErrorMessage("用法: /设置ui刷新频率 <毫秒>");
                args.Player.SendErrorMessage("示例: /设置ui刷新频率 5000 (5秒刷新一次)");
                args.Player.SendErrorMessage("最小值为1000毫秒(1秒)，最大值为60000毫秒(1分钟)");
                return;
            }
            
            if (!int.TryParse(args.Parameters[0], out int interval) || interval < 1000 || interval > 60000)
            {
                args.Player.SendErrorMessage("无效的刷新频率！必须是1000到60000之间的整数");
                return;
            }
            
            EconomyConfig.Instance.uiRefreshInterval = interval;
            EconomyConfig.Save(ConfigPath);
            
            // 重新初始化ui定时器
            InitializeuiTimer();
            
            args.Player.SendSuccessMessage($"ui刷新频率已设置为 {interval} 毫秒");
            args.Player.SendInfoMessage($"相当于每 {interval / 1000.0:F1} 秒刷新一次ui");
            
            // 记录到日志
            TShock.Log.Info($"管理员 {args.Player.Name} 将ui刷新频率设置为 {interval} 毫秒");
        }

        private void CheckuiRefreshRate(CommandArgs args)
        {
            var interval = EconomyConfig.Instance.uiRefreshInterval;
            args.Player.SendMessage("══════════ ui刷新频率 ══════════", Color.Cyan);
            args.Player.SendMessage($"当前刷新间隔: {interval} 毫秒", Color.White);
            args.Player.SendMessage($"相当于: {interval / 1000.0:F1} 秒", Color.White);
            args.Player.SendMessage($"每分钟刷新次数: {60000 / interval} 次", Color.White);
            
            if (args.Player.HasPermission("economy.admin"))
            {
                args.Player.SendMessage("使用 /设置ui刷新频率 <毫秒> 调整刷新频率", Color.Yellow);
            }
            
            args.Player.SendMessage("══════════════════════════════", Color.Cyan);
        }
        #endregion

        #region 插件生命周期
        public override void Initialize()
        {
            // 前置插件检查
            if (!CheckShouyuanPreReq())
            {
                TShock.Log.ConsoleError("【加载失败】修仙经济插件因缺少前置插件而无法加载！");
                TShock.Log.ConsoleError("请安装 XiuXianShouYuan.dll（修仙星宿系统）后重启服务器。");
                return;
            }
            
            // 加载配置和数据
            EconomyConfig.Load(ConfigPath);
            LoadEconomy(DataPath);
            InitializeAuction();
            InitializeuiTimer(); // 初始化ui定时器
            
            // 注册事件和命令
            ServerApi.Hooks.NpcKilled.Register(this, OnNpcKilled);
            PlayerHooks.PlayerPostLogin += OnPlayerPostLogin;
            PlayerHooks.PlayerLogout += OnPlayerLogout;
            
            InitializeCommands();
            
            TShock.Log.ConsoleInfo("═══════════════════════════════════════════");
            TShock.Log.ConsoleInfo("修仙经济系统 v1.0.2 已成功加载！");
            TShock.Log.ConsoleInfo($"经济系统状态: {(EconomyConfig.Instance.EconomyEnabled ? "已启用" : "已禁用")}");
            TShock.Log.ConsoleInfo($"商店物品数量: {EconomyConfig.Instance.ShopItems.Count}");
            TShock.Log.ConsoleInfo($"玩家经济数据: {EconomyPlayers.Count} 名玩家");
            TShock.Log.ConsoleInfo($"ui刷新频率: {EconomyConfig.Instance.uiRefreshInterval} 毫秒");
            TShock.Log.ConsoleInfo("═══════════════════════════════════════════");
            
            // 移除了在初始化时向所有玩家发送消息的代码，避免空引用异常
            // 消息将在玩家登录时发送
        }
        
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ServerApi.Hooks.NpcKilled.Deregister(this, OnNpcKilled);
                PlayerHooks.PlayerPostLogin -= OnPlayerPostLogin;
                PlayerHooks.PlayerLogout -= OnPlayerLogout;
                
                if (_uiRefreshTimer != null)
                {
                    _uiRefreshTimer.Stop();
                    _uiRefreshTimer.Dispose();
                }
                
                SaveEconomy(DataPath);
                SaveAuction();
                
                TShock.Log.ConsoleInfo("修仙经济系统已卸载");
            }
            base.Dispose(disposing);
        }
        
        private void OnPlayerPostLogin(PlayerPostLoginEventArgs args)
        {
            var data = GetPlayerEconomy(args.Player.Name);
            UpdateEconomyui(args.Player, data);
            
            if (EconomyConfig.Instance.EconomyEnabled)
            {
                args.Player.SendSuccessMessage("星晶经济系统已加载！使用 /星晶余额 查看余额");
                
                // 如果是新玩家且配置允许，发送欢迎信息
                if (data.StarCrystals.Count == 0 && EconomyConfig.Instance.GiveInitialCrystals)
                {
                    args.Player.SendInfoMessage($"你获得了 {EconomyConfig.Instance.InitialCrystals} 初始低等星晶！");
                }
            }
        }
        
        private void OnPlayerLogout(PlayerLogoutEventArgs args)
        {
            SavePlayerEconomy(args.Player.Name);
            statusManager.RemoveText(args.Player);
        }
        #endregion

        #region StatusManager
        private class StatusManager
        {
            private class PlayerStatus
            {
                public string Key { get; set; }
                public string Text { get; set; }
            }

            private readonly Dictionary<TSPlayer, List<PlayerStatus>> _playerStatuses = new Dictionary<TSPlayer, List<PlayerStatus>>();

            public void AddOrUpdateText(TSPlayer player, string key, string text)
            {
                if (!_playerStatuses.TryGetValue(player, out var statuses))
                {
                    statuses = new List<PlayerStatus>();
                    _playerStatuses[player] = statuses;
                }

                var existing = statuses.FirstOrDefault(s => s.Key == key);
                if (existing != null)
                {
                    existing.Text = text;
                }
                else
                {
                    statuses.Add(new PlayerStatus { Key = key, Text = text });
                }

                UpdatePlayerStatus(player);
            }

            public void RemoveText(TSPlayer player, string key)
            {
                if (_playerStatuses.TryGetValue(player, out var statuses))
                {
                    var item = statuses.FirstOrDefault(s => s.Key == key);
                    if (item != null)
                    {
                        statuses.Remove(item);
                        UpdatePlayerStatus(player);
                    }
                }
            }

            public void RemoveText(TSPlayer player)
            {
                if (_playerStatuses.ContainsKey(player))
                {
                    _playerStatuses.Remove(player);
                    player.SendData(PacketTypes.Status, "", 0, 0x1f);
                }
            }

            private void UpdatePlayerStatus(TSPlayer player)
            {
                if (!_playerStatuses.TryGetValue(player, out var statuses) || !statuses.Any())
                    return;

                var combined = new System.Text.StringBuilder();
                foreach (var status in statuses)
                {
                    combined.AppendLine(status.Text);
                }

                player.SendData(PacketTypes.Status, combined.ToString(), 0, 0x1f);
            }
        }
        #endregion
    }
    
    // 颜色扩展
    public static class StringExtensions
    {
        public static string Color(this string text, Color color)
        {
            return $"[c/{color.R:X2}{color.G:X2}{color.B:X2}:{text}]";
        }
    }
}