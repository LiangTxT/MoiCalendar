# MoiCalendar 受控公测发布清单

候选版本：`2.0.30-beta.1`。本清单未勾选项必须由发布负责人实际验证后记录结果，代码测试通过不等于线上验收。当前不授权自动部署。

## 本轮核验记录（2026-09-10）

- 只读检查线上公开配置：PublicBaseUrl 为 `https://polite-rock-09eddaf00.7.azurestaticapps.net/`，CloudBackend.Enabled=true，BaseUrl 为 `https://uddfuhwaubzhxzmtbwkw.supabase.co`，公开 key 存在（未输出）。这些值与当前计划一致，尚未验证 GitHub 下一次部署的变量是否一致。
- 本地浏览器验证帮助页、隐私页及页脚 `2.0.30-beta.1`；首页 320/768/1440 宽度未检测到页面横向溢出。这不是 Android/iPad 真机验收。
- 未使用真实账户测试发信、第二设备、删除或外部备份，以下线上旅程清单保持待验收。

## 构建与版本

- [ ] 确认发布 commit、负责人、日期、测试环境并保存验收记录。
- [ ] `Directory.Build.props` 中统一更新 Version、AssemblyVersion、FileVersion；页脚显示公测版本，诊断/备份显示对应数字版本。
- [ ] `dotnet test MoiCalendar.slnx` 全部通过；`dotnet build MoiCalendar.slnx` 成功。
- [ ] 生成真实生产配置后执行 Release publish、`npm run deploy:harden`、`npm run deploy:validate`。
- [ ] 核对产物无 Local/Managed 配置、localhost 后端、管理密钥或测试 key。不要部署使用 example.test 的本地验证产物。
- [ ] 保留上一版静态产物和配置；回退应用前确认其兼容当前数据库，不删除已应用迁移。

## Supabase 与 Azure 人工配置

- [ ] GitHub vars：`MOICALENDAR_PUBLIC_BASE_URL=https://polite-rock-09eddaf00.7.azurestaticapps.net/`、`MOICALENDAR_CLOUD_ENABLED=true`、`MOICALENDAR_CLOUD_BASE_URL` 为真实托管 Supabase HTTPS 项目地址、`MOICALENDAR_CLOUD_PUBLIC_KEY` 为该项目 Publishable/anon key、`MOICALENDAR_SUPABASE_REALTIME_PATH=/realtime/v1/websocket`。
- [ ] 仓库不能证明实际 GitHub vars 正确；在生产浏览器读取 appsettings.Production.json 核对主机和启用状态，不复制密钥到反馈中。
- [ ] 在本地清库验证全部迁移；生产先备份再应用 8 个版本迁移，包括 `20260911000000_production_security_hardening.sql`。绝不对生产执行 db reset。
- [ ] 验证 RLS、RPC grants、跨用户隔离、设备撤销与 Realtime publication。执行 `npm run supabase:smoke` 只测试本地环境。
- [ ] 部署 delete-account Edge Function；管理凭据只放服务器环境。设置 `MOICALENDAR_ALLOWED_ORIGINS=https://polite-rock-09eddaf00.7.azurestaticapps.net`。
- [ ] 生产 Supabase 开启邮箱验证，配置可投递 SMTP、发件人、邮件模板与限流；实测收信和密码恢复。
- [ ] Supabase Site URL：`https://polite-rock-09eddaf00.7.azurestaticapps.net`；Redirect URL：`https://polite-rock-09eddaf00.7.azurestaticapps.net/settings`。
- [ ] Microsoft Entra SPA Redirect URI：`https://polite-rock-09eddaf00.7.azurestaticapps.net/authentication/login-callback`，核对公开 Client ID，不创建浏览器 client secret。
- [ ] 需要开发联调时保留 `http://localhost:5262/settings`、`https://localhost:7104/settings`，以及两个站点各自的 `/authentication/login-callback`。不要添加宽泛生产通配符。
- [ ] 继续使用现有 Azure Static Web Apps 资源、GitHub secret 和 main 工作流；构建项目为 `src/MoiCalendar.App/MoiCalendar.App.csproj`，上传目录为 `src/MoiCalendar.App/bin/Release/net10.0/publish/wwwroot`，skip_app_build=true，api/output_location 为空。
- [ ] Azure SPA fallback 返回 index.html；刷新 `/settings`、`/sync-status`、`/help`、`/privacy`、`/terms` 和认证回调正常；未知页面可返回首页，缺失静态资源不返回 HTML。
- [ ] 检查实际 HTTP CSP/nosniff/referrer/frame 响应头，控制台无 CSP 阻断和 Service Worker 完整性失败。
- [ ] 如启用自定义域名，先配置 Azure DNS/TLS，再同步修改 PublicBaseUrl、Supabase Site/Redirect、Entra callback、Edge origin。当前不要求 app.moicalendar.com。

## 完整用户旅程（使用专用测试账户）

- [ ] 首次访问：加载提示、版本可见，无强制登录；新建/编辑/删除本地日程后刷新仍存在。
- [ ] 首次离线前完整加载；Windows、Android、iPad 在飞行模式重开并编辑，恢复网络后数据仍在。
- [ ] 注册 → 邮箱验证 → 登录 → 刷新恢复会话；验证链接过期、密码错误、密码恢复均有可理解提示。
- [ ] 第一台设备手动云同步；第二台同账户拉取，创建/更新/删除一致；核对云同步前已有本地日程的范围，不假定全量自动上传。
- [ ] 离线编辑后重连；重复点击/失败重试无重复日程；删除不复活；漏 Realtime 通知通过增量 Pull 恢复。
- [ ] 两设备修改同一日程：冲突刷新后仍在；分别验证保留本地、保留云端和稍后处理，不静默丢弃版本。
- [ ] 设备登记、稳定 ID、重命名、撤销后拒绝同步；用户 A 不能查看/修改 B 的设备或日历。
- [ ] 云导出仅含云数据；本地 JSON 导出/预览/恢复/撤销保护正常，不包含 token 或提供商密码。
- [ ] OneDrive 登录、连接与外部备份正常；明确读回合并行为及临时 Files.ReadWrite 全盘权限。
- [ ] WebDAV HTTPS/CORS/凭据配置、连接失败、外部备份与恢复路径正常。
- [ ] 删除专用测试账户：近期认证、确认、默认保留本地/选择删除本地两种路径；重试不误删其他账户，旧 outbox 不重建云数据。
- [ ] 退出登录仍可编辑本地；再登录正确恢复账户；失效会话、断网、限流、服务不可用均可恢复。
- [ ] 手机/平板/桌面宽度验证：320、768、1440 px；长邮箱/标题/设备名不挤出按钮；键盘焦点可见、表单标签和错误可读、弹窗焦点及关闭行为人工验收。

## 公开发布前必须审核

- [ ] 明确运营主体和真实支持邮箱/渠道，替换帮助页占位信息。
- [ ] 审阅 `/privacy`、`/terms` 草稿：适用地区、用户权利、处理目的、供应商、保存/删除周期、服务条款和费用；不得将草稿标为已完成法律审查。
- [ ] 明确 Supabase/Azure 日志、备份、副本保留策略；不要承诺端到端加密或立即清除所有副本。
- [ ] 重新检查仓库/历史/静态产物的秘密；如发现真实泄露应先轮换再处理历史。
- [ ] 决定公测人数、支持响应安排和暂停发布条件；数据丢失、跨账户读取或删除异常应停止扩大公测。
- [ ] 每次 push/PR/部署前依 AGENTS.md 提醒并确认继续保留临时 OneDrive Files.ReadWrite 权限。

相关说明见 docs/authentication-redirects.md、docs/production-security.md、docs/account-data-lifecycle.md。手动验收结果请记录日期、平台、版本和结果，不记录账号密码或日历内容。
