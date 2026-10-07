# 日程提醒

## 使用

在设置页的「日程提醒」中开启本地提醒或后台推送。新建/编辑日程选择不提醒、开始时、提前 5/15/30 分钟、1 小时或 1 天。默认不提醒，避免现有日程突然发通知。

全天日程默认在日程日期的 **07:00** 提醒，可在编辑器中自定义 00:00–23:59。时间属于提醒时区，不是午夜 UTC；提前 1 天保持前一天同一本地时间，跨夏令时也如此。全天日程一天只提醒一次，多天日程在开始日期提醒。重复日程每次提醒，单次删除的出现除外。

本地提醒每 15 秒检查一次，应用运行时可离线工作。系统通知不可用时降级为页面内提醒。设备休眠、后台节流或关闭网页会暂停本地计时；恢复后仅补发最近 5 分钟。后台任务每分钟扫描云端记录，允许 15 分钟内补发，最多重试 5 次。页面和后台共用本设备的 IndexedDB 去重记录，30 天自动清理。

系统通知会显示标题和时间，不包含描述和地点。后台使用标准 Web Push 加密消息和浏览器厂商的推送端点；私钥仅存在服务端。后台只知道已同步的日程，离线尚未同步的取消/改期不能立即阻止远端发送。系统省电、勿扰模式、网络和推送服务会影响延迟，不可作为紧急报警或准点保证。

退出账户立即在本地拒收原账户推送，并尽力注销服务端订阅。更换账户不自动开启推送。iPad 需要将 PWA 添加到主屏幕，并通过明确点击授权通知。

## 后台部署（本次未执行）

1. 保留现有前端部署方式；依次应用 `20261006010000_calendar_event_fields.sql`、`20261006020000_calendar_reminders.sql`、`20261006030000_push_reminders.sql`（若前一迁移已上线，不重复执行）。不要重置生产数据库。
2. 在可信的本机执行 `npx web-push generate-vapid-keys`，只向服务器的 secret 管理器录入密钥，不保存到源码、前端配置或聊天输出。
3. 配置函数秘密：`MOICALENDAR_VAPID_PUBLIC_KEY`、`MOICALENDAR_VAPID_PRIVATE_KEY`、`MOICALENDAR_VAPID_SUBJECT`（真实的 `mailto:` 联系地址）、`MOICALENDAR_PUSH_SCHEDULER_SECRET`（至少 32 字符随机值）。`SUPABASE_URL`、`SUPABASE_SERVICE_ROLE_KEY` 使用后端环境。公钥不是秘密，私钥和 service-role key 严禁进入浏览器。
4. `MOICALENDAR_ALLOWED_ORIGINS` 配置实际应用 Origin，逗号分隔。部署 `push-configuration` 和 `dispatch-reminders`，分别遵循 config.toml 的 JWT 配置；后者只接受独立调度密钥。不要把调度密钥赋给浏览器。
5. 在 Supabase 中启用 `pg_cron`、`pg_net`、Vault；在 Vault 保存 `moi_push_dispatch_url` 和 `moi_push_scheduler_secret`，然后手动执行 `supabase/deployment/schedule-reminders.sql`。脚本不包含真实凭据。
6. 发布前端后，用户登录并同步，在设置页明确点击开启推送。验证定时、全天、自定义时间、重复、单次删除、取消订阅、关闭网页及注销账户；检查 cron/http 执行状态。模拟发送测试通过不代表真实设备送达验证。

当前单轮扫描上限 5000 个有提醒且有订阅的日程，超过会返回失败而非宣称完成。个人日历范围内适用；扩大到公共服务前必须增加持久化分片调度，并监控超时/失败。完整备份保留提醒字段；ICS 导入/导出的 VALARM 尚未实现。

参考：[Web Push 标准能力](https://developer.mozilla.org/en-US/docs/Web/API/Push_API)、[Supabase 定时调用及 Vault](https://supabase.com/docs/guides/functions/schedule-functions)、[web-push](https://github.com/web-push-libs/web-push)、[rrule](https://github.com/jkbrzt/rrule)。
