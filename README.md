# Erratum 简体中文补丁

这是 Windows 版《Erratum》的非官方简体中文补丁。补丁基于 BepInEx 5，在运行时替换文本并加载中文字体；不修改游戏资源封包和存档。

## 安装

1. 打开仓库的 `payload` 目录。
2. 将其中的全部文件复制到 `Erratum.exe` 所在目录，不要额外套一层文件夹。
3. 正常启动游戏。补丁直接覆盖默认英文显示，不新增语言菜单。

如果游戏原本已有 BepInEx，请合并目录。卸载时只删除 `BepInEx\plugins\ErratumChinesePatch`；如果 BepInEx 也是随本补丁首次安装，再删除 `winhttp.dll`、`doorstop_config.ini`、`.doorstop_version` 和整个 `BepInEx` 目录。

## 兼容基线

- Windows x64 / Unity `2022.3.62f2`
- `Erratum.exe` SHA-256：`16572716a82b6f90fd5cf8e2989ec82d914df05f6fbd2f578e8158ea7b153577`
- `MainAssembly.dll` SHA-256：`7c96033297ad8e5037d2952a091b990a5f3388a3d06ece697e2320bd125b5e9a`

游戏更新后若补丁失效，请先核对版本。补丁不会把旧文本写回新版本的游戏封包。

## 仓库数据边界

仓库不包含游戏程序集、资源封包、贴图、音频、提取结果或英文原文表。`translation/strings.csv` 只保存运行时定位信息、原文 SHA-256、UTF-16 长度和中文译文；插件在玩家本机对游戏当前显示的文本计算哈希后匹配。Base64 只用于安全存放多行中文译文，不用于隐藏游戏数据。

`payload` 中的 BepInEx 与 Source Han Sans SC 均为可再分发的开源组件，并附带原许可证。游戏本体及其内容的权利归原权利人所有。

## 校验与构建

构建需要 Windows、PowerShell 和 .NET SDK，以及用户合法安装的游戏程序集。仓库放在游戏根目录的子目录时可直接运行：

```powershell
& .\tools\验证准备状态.ps1
& .\tools\生成最终补丁.ps1
```

如果仓库位于其他位置，显式指定游戏根目录：

```powershell
& .\tools\生成最终补丁.ps1 -GameRoot 'D:\Games\Erratum'
```

脚本会校验 746 条翻译规则、构建插件、更新 `payload` 中的 DLL，并生成忽略于 Git 的 `dist\Erratum-简体中文补丁.zip` 及 SHA-256。

## 目录

- `translation/strings.csv`：公开的哈希键中文译文表。
- `plugin-src/`：运行时插件源码。
- `tools/`：本地化校验与发布构建脚本。
- `payload/`：可直接复制到游戏根目录的补丁文件树。
- `docs/`：技术路线、数据边界、字体、依赖和验证记录。

本项目为非官方社区补丁，与 Half & Half Studios 无隶属或背书关系。项目自制代码按仓库根目录的 MIT License 发布；第三方文件按各自随附许可证发布。
