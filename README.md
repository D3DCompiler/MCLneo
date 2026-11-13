# MCLneo

面向 Windows 的 Minecraft 启动器，用于管理游戏、模组、整合包、存档和联机。

## 功能

- **游戏管理**：创建、切换、重命名和删除游戏，每个游戏拥有独立的模组、存档和配置。
- **版本与加载器**：安装原版、Fabric、Quilt、Forge 和 NeoForge，支持官方源与 BMCLAPI 镜像。
- **模组与整合包**：搜索 Modrinth、导入本地文件，自动处理依赖、启用和禁用。
- **存档与资源**：管理存档、资源包和光影，支持备份、导入和导出。
- **账户**：离线账户，以及 authlib-injector / Yggdrasil 第三方账户。
- **联机**：IPv6 直连优先，EasyTier 自动回退；支持在线玩家列表。
- **服务器**：管理服务器列表，查看在线人数和公开玩家名单。
- **Java 与修复**：扫描和下载 Java，查看日志、分析崩溃、检查游戏文件。
- **界面**：浅色/深色主题、强调色、圆角窗口和液态玻璃导航。

## 安装

从发布页下载最新安装包，运行后按提示安装。默认安装目录为 `D:\MCLneo\Launcher`。

## 系统要求

- Windows 10 1809 或更高版本 / Windows 11，64 位
- 安装游戏和下载资源需要网络连接
- EasyTier 联机需要管理员权限

## 数据

启动器数据默认保存在 `D:\MCLneo\data`，游戏默认保存在 `D:\MCLneo\game`。两者都可以在设置中修改；MCLneo 使用标准 Minecraft 目录结构，也可以直接指向已有的 `.minecraft`。

删除游戏或存档时会先移动到回收目录，不会立即永久删除。

## 说明

MCLneo 不附带 Minecraft 游戏文件或 Mojang 资源。Minecraft 是 Mojang Studios 的商标，本项目与 Mojang Studios、Microsoft 没有隶属或背书关系。
