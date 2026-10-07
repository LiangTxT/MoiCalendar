import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const markup = readFileSync(new URL('../../src/MoiCalendar.App/Pages/Settings.razor', import.meta.url), 'utf8');
const css = readFileSync(new URL('../../src/MoiCalendar.App/Pages/Settings.razor.css', import.meta.url), 'utf8');

test('设置分类的原有入口与目标保留', () => {
    for (const id of ['cloud-account-heading', 'cloud-devices-heading', 'external-backup', 'appearance-heading', 'backup-heading', 'calendar-transfer-heading']) {
        assert.ok(markup.includes(`href="settings#${id}"`), id);
        assert.ok(markup.includes(`id="${id}"`), id);
    }
    for (const href of ['sync-status', 'sync-status#diagnostics-heading', 'sync-status#backup-log-heading', 'privacy', 'help']) {
        assert.ok(markup.includes(`href="${href}"`), href);
    }
});

test('导航分类均衡、名称准确，统一行高并适配窄屏', () => {
    const nav = markup.match(/<nav class="settings-index"[\s\S]*?<\/nav>/)?.[0];
    assert.ok(nav);
    const groups = [...nav.matchAll(/<section>([\s\S]*?)<\/section>/g)].map(match => match[1]);
    assert.deepEqual(groups.map(group => (group.match(/<a /g) ?? []).length), [4, 3, 3, 2]);
    assert.match(nav, /settings#reminders-heading/);
    assert.deepEqual(groups.map(group => group.match(/<h2>(.*?)<\/h2>/)[1]),
        ['账户与外观', '备份与文件', '同步与诊断', '帮助与隐私']);
    assert.equal(new Set([...nav.matchAll(/href="([^"]+)"/g)].map(match => match[1])).size, 12);
    assert.match(nav, /<span>云账户<\/span>/);
    assert.match(nav, /<span>日历导入 \/ 导出<\/span>/);
    assert.match(nav, /<span>备份同步日志<\/span>/);
    assert.equal((nav.match(/aria-hidden="true">›/g) ?? []).length, 12);
    assert.match(nav, /settings-index-trailing"><SyncIndicator \/>/);
    assert.match(css, /\.settings-index \{[^}]*grid-template-columns: minmax\(0, 1fr\)/);
    assert.match(css, /@media \(min-width: 560px\)/);
    assert.match(css, /@media \(min-width: 1000px\)/);
    assert.match(css, /\.settings-index a \{[^}]*min-height: 44px;[^}]*line-height: 20px;/);
});

test('同步管理移至状态页，外观紧随设备，外部备份按提供商显示', () => {
    const statusPage = readFileSync(new URL('../../src/MoiCalendar.App/Pages/SyncStatusPage.razor', import.meta.url), 'utf8');
    assert.doesNotMatch(markup, /同步管理|Realtime 连接/);
    assert.ok(statusPage.includes('id="sync-settings"'));
    assert.ok(statusPage.includes('ShowCloudManagement="true" ShowBackupSync="false"'));
    assert.doesNotMatch(statusPage, /settings#sync-settings/);
    assert.match(markup, /<\/div>\s*<section class="settings-card appearance-settings"/);
    const providerArea = markup.slice(markup.indexOf('@if (selectedProvider == SyncProviderType.OneDrive)'), markup.indexOf('@if (!string.IsNullOrWhiteSpace(providerStatusMessage))'));
    assert.ok(providerArea.includes('id="onedrive-heading"'));
    assert.ok(providerArea.includes('ShowCloudManagement="false" ShowBackupSync="true"'));
    assert.match(providerArea, /else if \(selectedProvider == SyncProviderType.WebDav\)[\s\S]*WebDAV：开发中。/);
    assert.doesNotMatch(markup, /id="sync-settings"/);
});

test('账户表单支持原生校验和 Enter，不产生浏览器导航或重复提交', () => {
    assert.match(markup, /<form[^>]+@onsubmit="SubmitCloudAccountAsync"[^>]+@onsubmit:preventDefault/);
    assert.match(markup, /<form[^>]+@onsubmit="CompletePasswordResetAsync"[^>]+@onsubmit:preventDefault/);
    assert.equal((markup.match(/type="submit"/g) ?? []).length, 2);
    assert.match(markup, /SubmitCloudAccountAsync\(\) => isCloudAccountBusy \? Task.CompletedTask/);
    for (const id of ['cloud-email', 'cloud-password', 'cloud-confirm-password', 'cloud-new-password', 'cloud-confirm-new-password']) {
        const field = markup.match(new RegExp(`<input id="${id}"[\\s\\S]*?/>`))?.[0];
        assert.ok(field, id);
        assert.match(field, /required/);
        assert.match(field, /disabled="@isCloudAccountBusy"/);
    }
    assert.equal((markup.match(/aria-pressed=/g) ?? []).length, 3);
});

test('外观使用同名原生单选项，保持应用服务回调和键盘操作', () => {
    assert.match(markup, /type="radio" name="appearance-mode"/);
    assert.match(markup, /@onchange="\(\) => SelectAppearanceModeAsync\(option.Mode\)"/);
    assert.doesNotMatch(markup, /role="radio"/);
    assert.match(css, /input:checked \+ span/);
    assert.match(css, /input:focus-visible \+ span/);
    assert.match(css, /forced-colors: active/);
});

test('设置布局具有手机收敛、语义反馈和上传焦点，不隐藏危险操作', () => {
    assert.match(css, /@media \(max-width: 599px\)/);
    assert.match(css, /grid-template-columns: minmax\(0, 1fr\)/);
    assert.match(css, /\.settings-status.failure\) \{ color: var\(--v4-danger\)/);
    assert.match(css, /\.settings-status.success\) \{ color: var\(--v4-success\)/);
    assert.doesNotMatch(css, /display: none|box-shadow:|backdrop-filter:/);
    assert.ok(markup.includes('临时初始化模式已启用'));
    assert.ok(markup.includes('确认替换本地数据'));
    assert.ok(markup.includes('永久删除云账户'));
});

test('合并数据文件区域，按钮紧凑并在说明下一行，保留确认流程', () => {
    const area = markup.slice(markup.indexOf('<section class="settings-card data-files"'), markup.indexOf('</main>'));
    assert.equal((area.match(/class="settings-card/g) ?? []).length, 1);
    assert.equal((area.match(/class="data-file-group"/g) ?? []).length, 2);
    for (const method of ['ExportBackupAsync', 'ExportICalendarAsync']) {
        const button = area.match(new RegExp(`<button[^>]+@onclick="${method}"`))?.[0];
        assert.ok(button);
        assert.match(button, /settings-data-button/);
        assert.doesNotMatch(button, /primary-button/);
    }
    assert.equal((area.match(/<SettingsFileAction/g) ?? []).length, 2);
    assert.ok(area.includes('ConfirmRestoreAsync'));
    assert.ok(area.includes('ConfirmCalendarImportAsync'));
    assert.ok(area.includes('恢复会替换全部本地数据'));
    assert.ok(area.includes('重复项的处理方式'));
    assert.ok(area.includes('@restoreSourceName'));
    assert.match(css, /\.data-file-row \{[^}]*grid-template-columns: minmax\(0, 1fr\);/);
    const shared = readFileSync(new URL('../../src/MoiCalendar.App/wwwroot/css/v4.css', import.meta.url), 'utf8');
    assert.match(shared, /\.settings-shell \.data-files \.settings-data-button \{[^}]*width: 84px;[^}]*height: 32px;/);
    assert.match(shared, /@media \(pointer: coarse\)/);
});

test('导入恢复按钮与导出共用外观，文件提示在右侧，深层选择器有效', () => {
    const component = readFileSync(new URL('../../src/MoiCalendar.App/Components/SettingsFileAction.razor', import.meta.url), 'utf8');
    const style = readFileSync(new URL('../../src/MoiCalendar.App/Components/SettingsFileAction.razor.css', import.meta.url), 'utf8');
    assert.match(component, /<label for="@Id">@Label/);
    assert.match(component, /<InputFile id="@Id" accept="@Accept" disabled="@Disabled"[\s\S]*?OnChange="HandleFileChangedAsync"/);
    assert.match(component, /settings-file-action settings-data-button/);
    assert.match(component, /<\/div>\s*<span class="selected-file-name"/);
    assert.match(component, /selectedFileName = args.File.Name/);
    assert.match(component, /await OnChange.InvokeAsync\(args\)/);
    assert.match(component, /if \(args.FileCount == 0\) return/);
    assert.match(style, /:focus-within/);
    assert.match(style, /max-width: 100%/);
    assert.match(style, /opacity: 0/);
    assert.match(style, /position: absolute/);
    assert.match(style, /width: 100%/);
    assert.match(style, /height: 100%/);
    assert.match(style, /::deep input\[type="file"\]/);
    assert.doesNotMatch(style, /::?deep\(/);
    const shared = readFileSync(new URL('../../src/MoiCalendar.App/wwwroot/css/v4.css', import.meta.url), 'utf8');
    assert.match(shared, /var\(--v4-primary\) 6%, var\(--v4-surface\)/);
    assert.doesNotMatch(style, /display: none/);
    assert.doesNotMatch(component, /IJSRuntime|onclick/);
});
