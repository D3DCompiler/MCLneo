# MCLneo 架构说明

## 1. 目标与边界

MCLneo 把“启动器能力”放在与 UI 无关的 `MCLneo.Core` 中，WPF 端只负责组合服务、展示状态和对话交互。这样做的目的：

- 下载、安装、启动、存档、联机逻辑可以被测试和命令行复用；
- 以后替换 UI、增加 CLI 或做自动更新时不必重写核心；
- 避免把网络和文件系统操作绑在 WPF 的 Dispatcher 上。

当前支持 Windows x64。核心库本身是 `net10.0`，但安装/启动路径中的原生库选择、`java.exe` 和路径分隔符都按 Windows 处理。

## 2. 进程与项目

```text
MCLneo.App (net10.0-windows, WPF)
  Services/AppServices.cs     组合根
  Services/AppState.cs        跨页面共享状态、启动/房间生命周期
  Services/ElevationService   按需管理员重启
  Views/*.xaml                页面和对话框
        │
        ▼
MCLneo.Core (net10.0)
  AppPaths / SettingsService
  Infrastructure/  HttpService, DownloadService, MirrorEndpoints, AppLog, PathSafety
  Minecraft/       VersionCatalog, MinecraftInstaller, VersionResolver, LibraryResolver,
                   VersionRules, JavaService, LaunchService
  Instances/       InstanceService
  Mods/            ModrinthService
  Worlds/          NbtReader, WorldService
  Rooms/           RoomInvite, EasyTierManager, RoomService
```

`AppServices.CreateAsync()` 的顺序是：创建目录 → 日志 → HttpClient → 读取设置 → 构造下载/目录/安装/实例/Java/启动/资源/存档/联机服务。UI 在服务初始化完成后才显示主窗口。

## 3. 目录和实例模型

启动器数据默认放在 `D:\MCLneo\data`，游戏根目录默认 `D:\MCLneo\game`（均可通过设置或 `MCLNEO_HOME` 覆盖），遵循标准布局：

```text
<gameRoot>/
  versions/
    <instanceId>/
      <instanceId>.json         实例 profile（可能含 inheritsFrom）
      mclneo.instance.json      名称、MC 版本、加载器、时间
      mods/ saves/ config/ ...
    1.21.4/                     共享原版基础版本
      1.21.4.json
      1.21.4.jar
      .mclneo-base              标记：不要作为独立实例显示
  libraries/
  assets/
```

- **原版基础版本**只安装一次，放在 `versions/<游戏版本>`，带 `.mclneo-base`。
- **实例**是一个最小 profile：`{ id, inheritsFrom, type }`。这样可以复用基础版本的 jar、资源索引和运行库，同时拥有独立的游戏目录（模组/存档/配置隔离）。
- Fabric/Quilt profile 来自各自的 meta API，包含自己的 `mainClass`、`libraries`、`arguments` 和 `inheritsFrom`。
- `InstanceService.ScanAsync()` 跳过带 `.mclneo-base` 的目录；没有元数据的版本按 `inheritsFrom`、`mainClass` 和库名推断加载器，标记为“已导入”。

## 4. 下载与镜像

`DownloadService`：

- 基于 `HttpClient` + `SocketsHttpHandler`，默认 8 并发；
- 下载到 `*.part-*`，校验 SHA-1/大小后原子替换；
- 已存在的文件按大小和 SHA-1 复用；
- 3 次重试，指数退避；
- 通过 `IProgress<DownloadProgress>` 汇总完成数、字节数和当前文件。

`MirrorEndpoints.Rewrite()` 在 BMCLAPI 模式下只重写已知域名：

```text
piston-meta.mojang.com / launchermeta.mojang.com → bmclapi2.bangbang93.com
libraries.minecraft.net                          → bmclapi2.bangbang93.com/maven
resources.download.minecraft.net                 → bmclapi2.bangbang93.com/assets
```

客户端 jar 和版本 JSON 使用 BMCLAPI 的 `/version/<id>/client`、`/version/<id>/json` 端点，而不是简单替换 host。

## 5. 安装流水线

`MinecraftInstaller.InstallAsync()`：

1. 选择唯一实例 ID，创建实例目录。
2. `EnsureVanillaAsync`：
   - 读取版本 JSON（官方源或 BMCLAPI）；
   - 下载客户端 jar；
   - `LibraryResolver.Resolve` 生成运行库下载列表（规则、原生 classifier、Maven 路径）；
   - 下载资源索引和全部资源对象到 `assets/objects/<前两位>/<hash>`；
   - 下载 logging 配置；
   - 写 `.mclneo-base` 标记。
   - 如果基础版本已存在，会检查 JSON、jar、资源索引和运行库是否存在；缺文件时重新走检查/下载。
3. Vanilla：写最小 profile。
   Fabric/Quilt：调用 meta API 获取 loader 列表和 profile JSON，设置 `id/inheritsFrom`，下载 loader 库。
4. 创建实例目录、写 `mclneo.instance.json`。

进度是分阶段加权的 `InstallProgress`（准备、客户端、运行库、资源、日志、配置）。UI 只消费这个接口。

## 6. 版本继承与参数

`VersionResolver.Merge()` 递归合并 `inheritsFrom`：

- `libraries` 以 `group:artifact:classifier` 去重，子版本覆盖父版本；
- `arguments.game`/`arguments.jvm` 父在前、子在后；
- `mainClass`、`assetIndex`、`logging`、`javaVersion` 等标量由子版本覆盖；
- `inheritsFrom` 在合并结果中移除。

`LaunchService.PrepareAsync()`：

1. 解析有效版本，读取 `javaVersion.majorVersion`（缺失时按 MC 版本推断）。
2. 选择/安装 Java。
3. 确认资源索引存在。
4. `LibraryResolver` 过滤当前 OS/架构的原生库，解压 native jar 到实例 `natives/`。
5. 组装 classpath（运行库 + 客户端 jar；客户端 jar 可从父版本继承）。
6. 生成占位符：玩家、UUID、token、游戏目录、资源目录、natives、classpath、分辨率等。
7. 读取 `arguments.jvm` / `arguments.game`，处理规则和占位符；没有参数时使用旧版默认参数。
8. 添加 `-Xmx/-Xms`、编码、启动器品牌、Log4Shell 缓解参数，最后加 mainClass 和游戏参数。
9. 启动 `java.exe`，输出写入 `<实例>/logs/mclneo-game-*.log`，通过 `GameSession` 暴露输出/退出事件。

### 离线 UUID

使用 Java 的 `UUID.nameUUIDFromBytes("OfflinePlayer:" + name)` 等价算法（MD5 + version 3/variant），保证离线服务器识别稳定。

## 7. Java

`JavaService.DiscoverAsync()` 检查：设置中的路径、`JAVA_HOME`、PATH、常见的 Program Files JDK 目录、MCLneo 的 `runtimes/`。每个候选执行 `java -version` 解析主版本。

`EnsureAsync(requiredMajor)` 优先精确匹配，其次选择大于等于要求的版本；找不到时通过 Adoptium API 下载 Windows JRE（x64/arm64），解压到 `%APPDATA%\MCLneo\runtimes`。

## 8. 模组与资源

`ModrinthService` 直接使用 Modrinth v2 API：

- `/search` 的 facets 包含 `project_type`、`versions:<MC>`、`categories:<loader>`；
- `/project/{id}/version` 过滤游戏版本和加载器；
- 安装主文件到实例目录：mods / resourcepacks / shaderpacks；
- 递归安装 `dependency_type=required` 的依赖，深度上限 5，防止循环；
- 已安装列表从文件系统读取，支持 `.disabled` 重命名和删除；
- 从 `fabric.mod.json`、`quilt.mod.json`、`META-INF/mods.toml` 提取显示名/版本。

当前只接入 Modrinth；CurseForge 需要 API key，尚未接入。

## 9. 存档与实例包

`NbtReader` 是只读的大端 NBT 解析器，读取 gzip 压缩的 `level.dat`，提取 `Data.LevelName`、`Version.Name`、`LastPlayed`、`GameType`、`Hardcore`。

- 导出：把存档目录打包成 zip，条目为世界根目录下的相对路径；
- 导入：解压到临时目录、查找 `level.dat`、校验 zip 路径边界，复制到 `saves/<名称>`，重名自动加序号；
- 删除：移动到 `%APPDATA%\MCLneo\trash`；
- 实例包：`pack.json` + profile + 可选 `mods/config/resourcepacks/shaderpacks/saves/options.txt/servers.dat`。导入时重写 profile 的 `id`，若依赖的基础版本不存在则拒绝并清理临时目录。

## 10. 加载器、整合包、更新与皮肤

- `LoaderInstallerService` 下载官方 Forge/NeoForge installer，写入最小 `launcher_profiles.json`，执行 `java -jar installer --installClient <gameRoot>`，再创建继承安装器 profile 的隔离实例。加载器版本来自 Forge promotions 或 NeoForge maven-metadata。
- Forge/NeoForge 的 patched client 会替代 vanilla client，因此 `LaunchService` 对 Forge-like 主类不再把继承的 vanilla jar 加入 classpath，避免 NeoForge JPMS 模块重复。
- `ModpackService` 支持 Modrinth `.mrpack`：解析 `modrinth.index.json`，按 dependencies 安装加载器，下载 files，安全解压 `overrides` / `client-overrides`。本地 `.mrpack` 走同一路径。

## 11. 第三方登录、皮肤、服务器与日志

- `AuthlibService` 校验 authlib 元数据、调用 `/authserver/authenticate`，保存 AccessToken；启动时添加 `-javaagent:authlib-injector.jar=<server>`。只支持第三方账号，不实现微软 OAuth。
- `ServerListService` 直接读写 `servers.dat`（未压缩 NBT），支持 IPv4/IPv6 地址；启动参数在 1.20+ 使用 `--quickPlayMultiplayer`。
- `LogService` 读取实例 `logs` 与 `crash-reports`；UI 提供文本查看器和 `CrashAnalyzer` 常见特征归因。
- `SkinService` 通过 authlib session profile 或 mc-heads 获取皮肤预览，缓存到 D 盘。
- `UpdateService` 读取更新清单、校验 SHA-256、解压到 `D:\MCLneo\data\updates`，退出后由 detached cmd 替换文件并重启。
- `ThemeService` 加载 `Themes/Dark.xaml` / `Themes/Light.xaml`，把调色板写入本地资源以触发 DynamicResource 更新；切换时窗口先截图旧主题、再淡出叠层，形成约 320ms 交叉淡入。支持钴蓝/暖橙/紫罗兰/石墨强调色。

## 12. 联机房间

`RoomInvite` 包含网络名、密钥、中继地址、游戏端口，序列化为 `MCLN1:<base64url>`。邀请码与 PCL2 不互通。

`RoomService` 启动 EasyTier Core：

- 房主：`-i 10.114.114.114`、`--tcp/udp-whitelist <游戏端口>`、`-p <中继>`；
- 加入者：`-d`，通过 `--port-forward tcp/udp://127.0.0.1:<本地端口>/10.114.114.114:<游戏端口>` 把虚拟网络端口映射到本地；
- 监听端口按连续 5 个端口块预留（EasyTier 会从 base 派生 wg/ws/quic/faketcp），RPC 端口在块外选择；
- 加入者轮询本地转发端口，确认可达后显示 `127.0.0.1:<端口>`；
- 输出写入 `%APPDATA%\MCLneo\logs\room-*.log`。

EasyTier 需要 TUN 虚拟网卡，因此当前实现要求管理员权限。UI 检测未提权时显示警告和“以管理员身份重启”。这是运行时约束，不是可绕过的代码问题。

## 13. 线程模型与生命周期

- WPF UI 线程只做绑定和状态更新；
- 下载/安装/启动使用 async/await，进度通过 `Progress<T>` 回到 UI；
- 游戏进程退出事件来自线程池。**不能在 `Process.Exited` 回调里同步 `Dispatcher.Invoke` 后再 `Process.Dispose()`**：这会造成“UI 等 Process 锁、事件线程等 UI”的死锁。当前实现使用 `Dispatcher.BeginInvoke`，并在事件返回后延迟在后台线程 Dispose；
- 联机进程退出同样使用 `BeginInvoke`。

## 14. 扩展点

1. **Forge/NeoForge**：新增 `ILoaderInstaller` 实现，运行官方 installer jar 的 `--installClient`，生成/合并 profile；`MinecraftInstaller` 中替换当前的 `NotSupportedException`。
2. **微软登录**：新增 `IAuthService`，device code → XBL/XSTS → Minecraft services → profile；UI 账户页目前只有离线账户。
3. **CurseForge / 整合包**：增加 provider 抽象和 `.mrpack`/CurseForge zip 解析，统一依赖图。
4. **Java 管理页**：把 `JavaService.InstallAdoptiumAsync` 暴露为可选择版本、可删除运行时。
5. **修复/校验**：复用 `EnsureVanillaAsync` 的检查逻辑，在实例卡片提供“修复文件”。
6. **自动更新**：基于发布清单和签名校验，更新 App 目录，不动游戏数据。

## 15. 已知限制

- 只支持 Windows；原生库筛选按当前 Windows 架构。
- 旧版（1.6 之前）`virtual/map_to_resources` 资源目录尚未物化。
- 没有 OptiFine、CurseForge、微软登录、独立皮肤管理、自动更新、定时备份。
- authlib 登录依赖具体服务器兼容性，未使用真实第三方账号做端到端登录验证。
- 资源包/光影的版本依赖与冲突解决是基础的“最新兼容版本”，没有完整依赖图。
- EasyTier 房间需要管理员权限，且公共中继可用性不受本项目控制。
- 没有增量更新、断点续传（只有重试和已存在文件复用）、签名校验。
