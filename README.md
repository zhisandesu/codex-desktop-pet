# 柯朵桌宠 1.0 · 我将codex娘做成桌宠了

让 Codex 的工作状态变成桌面上看得见的小动作。

柯朵（Keduo）是一只住在 Windows 桌面上的小龙娘。她会在你写代码时陪着你：空闲时走来走去、坐下发呆、自己玩耍；任务忙起来时坐到电脑前工作，结束后再给你反馈。你也可以摸摸头、把她拎起来，或者打开旁边的小球和她聊天。

![柯朵桌宠 1.0：我将codex娘做成桌宠了](docs/media/keduo-1.0-cover-4x3.png)

[下载与版本记录](https://github.com/zhisandesu/codex-desktop-pet/releases) · [使用指南](docs/USER_GUIDE.md) · [反馈问题](https://github.com/zhisandesu/codex-desktop-pet/issues)

这是个人社区项目，不是 OpenAI 官方产品，也不附带 Codex 程序、开发者的 API Key、登录信息、个人设置或聊天记录。当前版本命名为 1.0；代码和角色素材尚未指定开源或再分发许可证，详见 [授权状态](LICENSE-STATUS.md)。版本更名不代表完成了所有环境的兼容性验收。

## 她能做什么

![柯朵桌宠的桌面互动、任务陪伴与中文聊天功能](docs/media/keduo-1.0-features-16x9.png)

- **桌面互动**：轻抚头部、单击弹脑门、双击问候，长按后按抓取位置触发不同反应；右键调整外观和功能。
- **自主动作**：走路、跑摔、攀爬、跳落、坐姿待机，以及捉迷藏、飞机手、玩尾巴等小动作。
- **任务陪伴**：连接本机 Codex 后，对任务忙碌、等待确认、完成和失败作出反馈；任务情境反馈可关闭。
- **中文聊天与记忆**：通过本机已登录的 Codex 会话聊天，可管理角色设定和本地重点记忆。她自己的聊天房间与普通任务分开。
- **可选语音**：配置自己的服务凭据后使用云端识别与朗读；另有本机朗读和可选本地识别回退。

仅体验角色动画和鼠标互动不需要填写开发者 API Key。连接、对话和语音各自有账号、模型或硬件条件，不能把本项目理解为附赠模型额度或离线大模型。

项目采用 C# / WPF，当前包含 45 组正式动画目录、4,740 张动画帧。动画资源是下载体积的主要部分。

## 使用便携版

1. 取得 `Keduo-1.0-win-x64-portable.zip` 后，完整解压到有写入权限的目录。普通使用者无需下载源码包。先阅读包内 `先读我-使用说明.md`；仓库公开发布后，可从 [Releases](https://github.com/zhisandesu/codex-desktop-pet/releases) 获取后续版本。
2. 双击 `Start-Keduo.cmd` 或 `XiaobianPet.exe`。Windows x64；便携版包含 .NET 运行时，不需单独安装 .NET。
3. 先体验角色动画和鼠标互动。连接 Codex 的功能需要你自己安装、登录兼容的 Codex 桌面端/CLI；动态台词依赖账号可用模型及额度。未连接时部分功能不可用。
4. 云端语音可选：运行 `Configure-Voice.cmd`，输入你自己的方舟 Agent Plan 凭据，隐藏输入并保存到你自己的 Windows 凭据管理器。没有该凭据时语音朗读可回退到本机 SAPI。
5. 本机识别回退可选：`Setup-Local-ASR.cmd` 会安装 Python 依赖并下载约 1.6 GB 模型；不作为启动前置条件。此脚本针对 CUDA/GPU 方案，请先检查自己的硬件与磁盘空间。

本版未签名；请先核对发行来源和 SHA256。不要为了运行程序关闭系统防护。

下载页的 `SHA256SUMS.txt` 可用于校验压缩包。在 PowerShell 中运行 `Get-FileHash .\Keduo-版本-win-x64-portable.zip -Algorithm SHA256`，把结果与对应文件名的校验值比较。

### 第一次见面

悬停在头部并连续左右轻抚可以摸头；拖动前先长按片刻。聊天从角色旁边的小球进入，输入“柯朵，我今天把项目做完了”即可尝试直接对话。右键菜单可以管理语音、动态台词、长期记忆和外观。

目前仅提供 Windows x64 版，不提供 macOS、Linux 或手机安装包。真实账号连接、麦克风与音色效果受本机环境影响；遇到问题请附上版本、系统和复现步骤，不要上传密钥或完整聊天记录。

## 功能与隐私

- 点击、摸头、长按拖动、走路、跑摔、坐姿、忙碌与自主游戏动画。
- 旁边的小球打开聊天与任务面板；右键打开功能和外观设置。
- Codex 文本对话复用本机登录；本程序的 Codex 对话链路不读取 `OPENAI_API_KEY`。
- 云端语音会把当前语音内容发送给相应服务；只在你主动启用并配置后使用。模型和语音服务的可用性、计费与使用限制由各服务决定。
- 数据存于 `%LocalAppData%\XiaobianPet`，不在程序目录。卸载程序文件不会自动删除个人记忆或 Windows 凭据。
- 不要分享本机数据目录、账号登录文件、录音缓存或完整诊断日志。

详细交互见 [使用指南](docs/USER_GUIDE.md)。该指南保留开发工作区操作说明，其中开发启动脚本路径不适用于便携包；便携包以本页为准。

## 开发

需要 Windows、.NET 9 SDK、Python 3.11+ 和 Git LFS。当前代码保持原有 net9.0-windows，未在打包时强行迁移框架。

```powershell
git lfs install
git lfs pull
dotnet restore .\XiaobianPet\XiaobianPet.csproj --locked-mode
python .\tools\release.py scan
python .\tools\release.py test
dotnet run --project .\XiaobianPet\XiaobianPet.csproj
```

动画由 Git LFS 管理。克隆后必须取回真实 PNG；打包检查会拒绝 LFS 指针或缺帧。[Git LFS 官方说明](https://docs.github.com/en/repositories/working-with-files/managing-large-files/collaboration-with-git-large-file-storage)；上传前请检查自己账号的存储和流量额度。用户下载便携版不需安装 Git LFS。

## 打包与更新

修改 `version.json` 和 `CHANGELOG.md`，先运行检查和离线测试，然后：

```powershell
python .\tools\release.py package
```

生成 `release-output/版本/`：便携 ZIP、源码 ZIP、封面与说明 ZIP、SHA256SUMS.txt 和发行文件清单。已有同版本目录不会覆盖；需要修复重打时使用新版本。对外版本和文件名使用 1.0，.NET 构建版本自动补齐为 1.0.0。

更新程序时退出旧版、解压新版到新文件夹。个人配置仍使用同一个 LocalAppData 目录；建议更新前自行备份该目录，备份不能上传仓库。暂不包含在线自动更新器。

[发布与维护流程](docs/RELEASING.md) · [安全说明](SECURITY.md) · [授权状态](LICENSE-STATUS.md)

## 后续维护

本项目会继续更新。当前维护重点是交互和动画衔接、Codex 版本兼容、首次配置体验，以及后续的 .NET LTS 迁移；这里列的是维护方向，不是已完成的功能或发布时间承诺。

欢迎通过 Issues 描述问题和建议。涉及账号、隐私或密钥的问题，请先阅读安全说明。授权范围尚未确定时，请先讨论代码贡献或角色素材再分发，不要把本仓库标为 MIT 等开源许可证。
