# Cloudflare Best IP 节点导入能力删减

## 已确认范围

用户要求从 Cloudflare Best IP 功能中彻底移除生成／导入代理节点的能力，而不是仅隐藏入口。保留候选 IP 获取、测速、结果表、保存设置和显式点击的结果上报；现有代理节点不作删除或修改。先前报告的 `0/0` 候选问题属于独立诊断，不在此次改动范围内。

## 设计

- WPF 与 Avalonia 窗口同时移除 Import 页签及 Import profiles 按钮和事件绑定；说明文案改为只描述测速及可选上报。
- 共享 ViewModel 移除模板加载、协议／凭据／WebSocket 输入、导入状态和 `ImportAsync`。删除只服务于该功能的 `CfResultExporter`、`CfProfileTemplate` 与界面文案。
- 从 `CfBestIpItem` 移除模板 ID、协议、UUID／密码、WebSocket 路径字段及其初始化；既有配置读取时忽略这些旧字段，下一次保存时不再写入。测速所需字段和上报 URL 保持不变。
- 移除导入专用测试和当前用户文档中的导入说明；v2rayN 通用节点导入、WebAPI 和历史设计记录不受影响。

## 验证

检查 Cloudflare 范围内不再存在节点导入入口或可调用的导出器；测试旧配置含导入字段时仍可读取测速设置并在重新序列化后不写回旧字段。运行 ServiceLib 测试和 WPF、Avalonia 构建，确认页面绑定仍有效且手动 Report 保留。
