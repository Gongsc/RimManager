# RimManager

RimManager 是面向 Steam 版 RimWorld 的 Windows 模组管理器和启动器。当前版本为 v0.1.0。

## 使用

1. 解压 Windows x64 发布包并运行 `RimManager.exe`。应用自动检测 Steam 和 RimWorld。也可在「设置」中指定游戏目录、Steam.exe 和 ModsConfig.xml。
2. 点击模组查看详情，或用「启用所选」「停用所选」调整清单。在中栏拖动行，或打开自动排序预览。
3. 点击「应用配置」将顺序写入游戏设置。写入已有配置时，上一份配置备份到原文件旁的 `ModsConfig.xml.rimmanager.bak`。可用「恢复备份」载入旧顺序。
4. 在底部填写自定义启动参数，点击「启动游戏」。有待应用的更改时，按钮会在写入配置成功后启动游戏。

导入和导出采用带格式版本号的 JSON 清单，例如 `{"version":1,"mods":["ludeon.rimworld","brrainz.harmony"]}`。导入会用匹配项替换已启用清单并保留其相对顺序，待用户应用后才写入游戏配置。

## 创意工坊更新

检查更新会比较 Steam 工坊发布时间和本地工坊清单里的修订时间。下载使用独立安装的 [SteamCMD](https://developer.valvesoftware.com/wiki/SteamCMD)，可在设置中指定 `steamcmd.exe`。首版支持允许匿名下载的条目。更新前请退出游戏。原目录备份保存在 Steam 游戏库的 `steamapps/workshop/content/RimManagerBackups` 下。

可从模组详情锁定版本。锁定状态保存在当前 Windows 用户的设置中，适用于 RimManager 发起的选择及批量更新。Steam 客户端可独立同步工坊内容。首版检查更新针对 Steam 工坊安装项。

## 构建和发布

使用 Windows 和 .NET 8 SDK。

```powershell
dotnet build -c Release -warnaserror
dotnet publish -c Release -r win-x64 --self-contained true -o artifacts/app
```

GitHub Actions 在提交和拉取请求中构建并打包 Windows x64 产物。推送与项目版本一致的 `v*` 标签后，工作流使用 [v0.1.0 发布说明](release-notes/v0.1.0.md) 对应的同名说明文件创建 Release 并上传 ZIP。

## 许可证

本项目采用 [MIT 许可证](LICENSE)。
