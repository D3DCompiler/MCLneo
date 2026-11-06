# 第三方组件与数据来源

MCLneo 自身不包含 Minecraft 游戏文件、Mojang 资源或 PCL2 的代码/图片。

## Minecraft / Mojang

- 版本清单、客户端、运行库和资源文件由 Mojang 的公开 CDN 或 BMCLAPI 镜像按需下载。
- Minecraft 是 Mojang Studios 的商标。MCLneo 与 Mojang Studios、Microsoft 没有隶属或背书关系。
- 使用 Minecraft 需要遵守 Minecraft 最终用户许可协议（EULA）。

## BMCLAPI

- 下载源：<https://bmclapi2.bangbang93.com/>
- 作为 Mojang CDN 的国内镜像使用。请求失败时用户可以切换到官方源。

## Fabric / Quilt

- Fabric Meta：<https://meta.fabricmc.net/>
- Quilt Meta：<https://meta.quiltmc.org/>
- 加载器与安装配置遵循各自上游项目的许可。

## Modrinth

- API：<https://api.modrinth.com/v2>
- 模组、资源包、光影的元数据和文件由 Modrinth 及其作者提供。
- 安装前请遵守各项目的许可和整合包分发规则。

## EasyTier

- 项目地址：<https://github.com/EasyTier/EasyTier>
- MCLneo 在用户首次使用联机房间时按需下载 EasyTier Core，不把二进制打包进仓库。
- EasyTier 的许可、隐私和网络行为由该项目负责。联机需要管理员权限创建 TUN 虚拟网卡。
- 公共中继节点是第三方基础设施，可用性和隐私策略不受 MCLneo 控制。

## Forge / NeoForge

- Forge：<https://minecraftforge.net/>
- NeoForge：<https://neoforged.net/>
- MCLneo 下载并执行官方 installer；加载器代码和安装产物遵循各自上游许可。

## authlib-injector

- 项目地址：<https://github.com/yushijinhun/authlib-injector>
- 运行时按需下载，用于第三方 Yggdrasil 认证注入。第三方账号的密码不会保存；AccessToken 保存在本机配置目录。

## Eclipse Adoptium

- API：<https://api.adoptium.net/>
- 当本机缺少所需 Java 版本时，MCLneo 可以从 Adoptium 下载 JRE。

## PCL2

- Plain Craft Launcher 2（PCL2）由龙腾猫跃开发，是独立项目。
- MCLneo 是独立实现，不包含 PCL2 的源代码、素材或二进制文件，也不代表 PCL2 官方。
- MCLneo 的联机邀请码与 PCL2 的邀请码不互通。
