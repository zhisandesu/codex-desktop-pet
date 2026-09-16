# 发布与长期维护

## 首次上传

仅上传本干净仓库目录，不上传原始桌面工作区。建议先建私有仓库，确认代码与角色素材授权后再公开。不要上传 `release-output`；压缩包作为 Release 附件发布。

```powershell
git init -b main
git lfs install --local
python tools/release.py scan
git add .
git status --short
git diff --cached --stat
git commit -m "Prepare initial preview release"
# 在 GitHub 建立空仓库后，把下面的占位符替换为自己的仓库地址
git remote add origin https://github.com/OWNER/REPOSITORY.git
git push -u origin main
```

GitHub 网页拖文件不适合该仓库的动画集。LFS 会保存并传输大文件，请检查账号配额。此导出目录不含旧 Git 历史，避免把旧文件通过历史带出去。

## 之后每次更新

1. 选定一个代码源：建议将干净 Git 仓库作为新的日常开发目录。如果仍在旧工作区修改，请通过旧工作区 `release-tools/export_public.py --name 新目录名` 导出新候选，然后审阅差异后合入 Git 仓库。不要用整个旧目录覆盖新仓库。
2. 更新动画时同时重建 `animation-manifest.json` 的 SHA256；沿用旧工作区导出器可自动完成。代码和动画配置必须同批提交。
3. 改依赖后有意更新并提交 `packages.lock.json`（`dotnet restore XiaobianPet/XiaobianPet.csproj --force-evaluate`）。普通发布使用 locked mode，依赖不匹配就失败。随包运行时补丁固定在 Directory.Build.props，当前为 9.0.20；发布前检查微软安全更新，升级后重新恢复、测试和打包。NAudio 升级后需重新核查扫描器中的供应商 DLL 哈希。
4. 更新 `version.json`、CHANGELOG、README 和 QUICKSTART；为新版本准备 `docs/RELEASE-版本.md`、`docs/media/keduo-版本-cover-4x3.png` 和 `docs/media/keduo-版本-features-16x9.png`。运行 scan、test、package；缺少对应版本的介绍或图片时打包会停止，避免混入旧版本封面。测试仅选离线 harness，账号连接测试不在默认列表内。
5. 在自己机器上做启动、无凭据启动、点击/拖拽、外观、退出与旧配置迁移检查；换一台没有开发工具的 Windows x64 机器验收便携包。
6. 提交并推送；创建与版本号一致的 `v版本` Git tag。工作流构建、检查，再创建 **草稿** Release，人工验收附件和授权后才发布。

发布 ZIP 不是自动更新服务。暂采用新目录解压更新，避免覆盖运行中的程序和丢失用户数据。

## 工程边界与后续事项

- 当前保持 net9.0-windows；.NET 9 支持至 2026 年 11 月，应在此之前单独安排 .NET 10 LTS 迁移和完整回归，不在首次安全打包中混入框架升级。[微软支持周期](https://learn.microsoft.com/en-us/dotnet/core/releases-and-support)
- Actions 使用固定提交 SHA，Dependabot 提醒更新；审批更新后再合并。
- 本项目依赖 Codex 桌面桥接行为，不能保证未来桌面版本持续兼容，升级 Codex 后需要连接回归。
- 首次发布前确认许可证、素材权利、仓库公开范围；如需大范围下载，另行评估签名、自动更新和分发流量。

## 发布检查单

- [ ] 授权与素材再分发已确认
- [ ] API/账号/个人数据未进入文件或 Git 历史
- [ ] 动画已通过 LFS 上传并能从新克隆取回
- [ ] 离线测试及构建通过，ZIP 校验通过
- [ ] 干净机器人工启动验收完成
- [ ] 版本、更新记录和 Release 附件一致
