# 云账户与设备管理

MoiCalendar 的“我的设备”使用本地 `DeviceIdentity` UUID 和云端 `devices` 表识别一个浏览器安装。UI 只依赖提供程序无关的 `ICloudDeviceService`；Supabase REST/RPC、认证令牌和 JSON 响应都封装在基础设施实现中。

## 登记与同步时间

设备在登录后首次执行普通增量云同步时登记。重复使用相同 DeviceId 登记是幂等的，不会覆盖用户已经修改的设备名称。设备 ID 继续保存在 IndexedDB 的 `deviceIdentity` store 中，升级、刷新和正常重启不会更换它。

只有完整增量同步成功并且本地 IndexedDB 已经提交新游标后，同步引擎才调用设备确认 RPC。该 RPC 更新 `last_seen_at` 和 `last_acknowledged_revision`；设置页不能任意写入“最后成功同步”时间。

## 所有权与撤销

`devices` 表继续启用 RLS，直接查询只能返回 `owner_id = auth.uid()` 的记录。客户端不再拥有对设备表的直接 INSERT/UPDATE 权限；登记、重命名、撤销和同步确认全部通过校验 `auth.uid()` 的数据库函数完成。

撤销会设置 `deleted_at` tombstone。新的 Push、Pull 和同步确认都会验证 DeviceId 仍属于当前用户且没有被撤销。被撤销设备上的 IndexedDB 日历和 SyncOutbox 不会被删除，但该 DeviceId 不能继续正常云同步。

## 重要限制：不是 Auth 会话撤销

设备撤销是 **MoiCalendar 应用级撤销**，不是 Supabase Auth session 撤销。浏览器中仍然有效的 Supabase 登录会话不会因此被服务端注销，设置页也不会宣称已经注销该会话。

如果被撤销设备的使用者仍掌握账户密码或有效登录会话，清除站点数据或重新安装后可以生成新的 DeviceId，再次作为新设备登记。要同时撤销底层 Auth session，需要可信的服务器端管理逻辑和仅在服务端保存的权限凭据；此类能力不得放入 Blazor WebAssembly 客户端，当前模块没有实现。

OneDrive 和 WebDAV 是独立的可选备份提供程序，不参与云设备登记或撤销。
