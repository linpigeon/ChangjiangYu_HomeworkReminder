# 长江雨课堂接口实测记录

全部结论来自本机实测（`changjiang.yuketang.cn`，账号 `uv_id=3214`，`term=202601`）。
**这不是官方文档**——雨课堂没有面向学生的公开 API，以下端点均来自网页端自身调用，
无契约保证。标注「未验证」的项目表示本次没有确证。

## 通用约定

| 项目 | 值 |
|---|---|
| 域名 | `https://changjiang.yuketang.cn` |
| 认证 | Cookie 会话，**仅凭 `sessionid` 即可**驱动全部接口 |
| 必需请求头 | `xtbz: ykt` |
| POST 额外要求 | `X-CSRFToken: <csrftoken cookie>`，否则 403 |
| 部分接口要求 | `classroom-id: <room>` 请求头（`leaf_info` 缺失时返回 `Objects does not exist.`） |
| 时间戳 | 毫秒；按北京时间（UTC+8）解读。`0` 表示无值 |
| 节流 | 单账号串行 + 每次约 200ms 延迟，未触发风控 |

会话失效的表现：`/v2/api/web/*` 返回 `401` + `{"errcode":401002,"errmsg":"Cookie has no sessionid"}`。

---

## 1. 课程列表

```
GET /v2/api/web/courses/list?identity=2
```

返回 `data.list[]`，每项含 `classroom_id`、`name`、`term`、`course.id`。
本账号共 39 门，其中 `term=202601` 有 16 门。

## 2. 学习日志（作业与公告的发现入口）

```
GET /v2/api/web/logs/learn/{classroom_id}?actype=-1&page=0&offset=500&sort=-1&term=latest&uv_id=3214
```

> **`offset` 是「每页条数」，不是偏移量。** 按偏移量理解会只拿到默认条数。

`data.activities[]` 的关键字段：

| 字段 | 说明 |
|---|---|
| `type` | 见下表 |
| `id` | 日志 id |
| `title` | 事项标题 |
| `create_time` | 毫秒时间戳 |
| `hasRead` | **仅 type=9 公告有**，未读为 `false` |
| `content` | 作业内容体（type=19 有，type=5 **为 undefined**） |
| `content.score_d` | type=19 的截止时间 |
| `content.leaf_id` | type=19 的叶子 id |
| `is_finished` | 上课记录的完成标记 |

### 日志 type

| type | 名称 | 截止时间来源 | 本账号条数 |
|---|---|---|---|
| 5 | 试卷类作业 | 章节树 `leaf_info` 的 `score_deadline` | 7 |
| 9 | 公告 | 无 | 8 |
| 14 | 上课 | 无 | 29 |
| 19 | 章节作业 | 日志自带 `content.score_d` | 7 |

**type=5 与 type=19 是两个互不重叠的发布渠道，必须都收**，否则会漏掉一半作业。

## 3. 章节树（type=5 的截止时间唯一来源）

```
GET /mooc-api/v1/lms/learn/course/chapter?cid={classroom_id}&term=latest&uv_id=3214&classroom_id={classroom_id}
```

`data.course_chapter[].section_leaf_list[]`，每个叶子：

```json
{ "id": 48064815, "leafinfo_id": 48067036, "name": "概率论与数理统计B作业 第1次",
  "score_deadline": 1789128000000 }
```

> ### ⚠ 关键：用 `id`，不要用 `leafinfo_id`
>
> 章节树里每一项同时有 `id` 与 `leafinfo_id`，两者是不同体系的 id。
> `leaf_schedules` 的键、以及 `/leaf_info/{room}/{id}/` 的 `{id}`，**用的都是 `id`**。
>
> 实测对照：
> - `leaf_info/26109213/48064815/` → 200，`name="概率论与数理统计B作业 第1次"`，`publish_time=1788758431000`
> - `leaf_info/26109213/48067036/` → `{"error_code":404000,"msg":"Objects does not exist."}`
>
> 用错会导致进度查不到（完成状态全部退化成「未知」）且详情接口报错。

叶子与日志的关联靠**标题**（`name` == `title`）。type=5 日志没有内容体，只能这样匹配；
type=19 也可以顺带用它校正并补齐截止时间。

## 4. 作业详情

```
GET /mooc-api/v1/lms/learn/leaf_info/{classroom_id}/{leaf_id}/
Header: classroom-id: {classroom_id}
```

返回 `data.publish_time`（发布时间）、`data.score_deadline`、`data.leaf_type`、`data.is_assessed`。

## 5. 完成进度 ★

```
POST /mooc-api/v1/lms/learn/course/pub_new_pro?cid={room}&term=latest&uv_id=3214&classroom_id={room}
Header: X-CSRFToken: <csrftoken>
Body:   {"classroom_id":{room},"cid":{room},"term":"latest","uv_id":3214}
```

`data.leaf_schedules` 是 `{ "<leaf_id>": 值 }` 的字典，**值的形态有两种**：

```json
{ "47585656": 1,
  "48064815": { "status": 3, "total": 1, "done": 1, "score": 0 },
  "48492377": { "status": 1, "total": 0, "done": 0, "score": 0 } }
```

- **对象形态**：可判定。`done === 0` → 未开始；`0 < done < total` → 进行中；`done === total` → 已完成。
- **数字形态**（实测值为 `1`）：该 leaf 没有作答内容，**无法区分未开始与已完成**。
  本应用对此报「未知」而不是猜测。
- `total: 0` 表示题目尚未组卷（页面显示 `0/0`），此时按 `done === 0` 判为未开始。

> `/course/schedule` 返回同样的数据，但**要求会话级 `sign` 参数**
> （页面 URL 里 10 位随机串），外部脚本拿不到，因此改用 `pub_new_pro`：它不需要 sign，
> 但必须是 POST 且带 CSRF 头（用 GET 得 405，漏 CSRF 得 403）。

## 6. 通知中心

> 注：本列表接口已从应用代码中移除（公告改走学习日志，见第 2 节），当前仅保留
> 未读数与「全部标记已读」两个调用。以下内容留作接口参考。

```
GET /smart_education/notification/message_assistant/user_messages/?term=latest&uv_id=3214&page=1&page_size=50[&filter_type=N]
```

`data` 含 `count`、`max_page`、`current_page`、`data[]`。单条字段：

```json
{ "filter_type": 3, "is_read": false, "title": "...", "course_name": "...",
  "classroom_name": "...", "msg_time": "2026-09-15 15:49", "notify_id": 15526565,
  "user_notify_id": 525889950, "end_time": "2026-09-17/10:20" }
```

**注意时间格式**：`end_time` / `publish_time` / `lesson_start_time` 用
`yyyy-MM-dd/HH:mm`（斜杠分隔），不是标准格式，需要自定义解析。

### filter_type（`filter_type` 只接受单值，传多个不会叠加）

| 值 | 含义 | 本账号条数 | 附加字段 |
|---|---|---|---|
| 1 | （等价于不筛选） | 609 | — |
| 2 | 上课提醒 | 383 | `lesson_start_time`、`lesson_status` |
| 3 | 考试 / 试卷类作业 | 133 | `end_time` |
| 4 | 公告 | 65 | `publish_time` |
| 6 | 作业 | 21 | `end_time` |
| 9 | 其他 | 7 | — |

### 未读总数

```
GET /smart_education/notification/message_assistant/user_summary/?term=latest&uv_id=3214
→ {"msg":"","data":{"total_unread":609},"success":true}
```

> **`is_read` 不可靠**：本账号 609 条里几乎全部为 `false`（老师发布即未读），
> 其中 383 条是上课提醒。所以公告的未读判据应使用**学习日志的 `hasRead`**。
>
> 需要 `classroom_id` 吗？不需要——但 `/v2/api/web/*` 前缀会做参数校验，
> 缺参会返回 `errcode 400002`，容易误判成端点存在（见「易踩的误判」）。

### 已读反写

```
POST /smart_education/notification/message_assistant/user_read_all/?term=latest&uv_id=3214
```

**这是唯一的已读反写接口**（全部已读）。实测前端对单条消息只改本地状态：

```js
this.$set(t, "is_read", !0)   // 仅前端 Vue 状态，无服务端调用
```

因此本应用不做单条反写。`POST` 的具体响应未验证（本应用只在用户确认后调用，且失败会提示）。

### 其他存在的端点（来自前端端点表，未逐一验证）

```
GET  /smart_education/notification/message_assistant/user_silence_status/
POST /smart_education/notification/message_assistant/user_silence_switch/
GET  /smart_education/notification/message_assistant/user_message_url/   # 需要 user_notify_id
GET  /api/open/is_login/          → {"data":{"is_login":false}}
GET  /v2/api/web/userinfo         → errcode 0 表示已登录；未登录 401
```

## 7. 登录

雨课堂的登录 UI **完全外包**，主 bundle 只做「401 → 跳登录页」：

```js
if (微信内置浏览器) location.href = "/login/weixinapp/?next=" + encodeURIComponent(path)
else window.dispatchEvent(new CustomEvent("ykt_login_dialog", {...}))   // iframe 弹窗
```

| 路径 | 行为 | 桌面端可用？ |
|---|---|---|
| `/login/weixinapp/?next=/web` | **302 跳到 `open.weixin.qq.com` OAuth**（`snsapi_userinfo`） | ✗ 微信内专用 |
| `/web?ykt_ai_login` | 登录页，内含 416×510 的 iframe | ✓ |
| `/authorize/wx-qrlogin` | **独立的二维码登录 SPA**（`authorize.0084e32a.js`） | ✓ |

登录页把 `/authorize/wx-qrlogin` 嵌进 iframe，扫码成功后通过
`postMessage("login_success")` 通知外层。**本应用直接打开 `/web?ykt_ai_login`**，
不自己复刻登录协议。

> 主 bundle（`student-corpus.js`，15.6MB）里**搜不到任何扫码 API**：
> `wxcode`/`wechat-auth-param`/`qrcode_url`/`check_login` 等 40+ 关键词全部 0 命中。
> 二维码实现被切到登录页专属 bundle。详见 [`docs/research/ykt-login-flow.md`](research/ykt-login-flow.md)。

### 其他登录方式（未验证）

```
/pc/web_login      /pc/web_logout      /pc/send_sms_code
/api/v3/user/login/app-web-pre-info    # 二维码预信息
/api/v3/user/login/app-web-login       # 扫码确认
/passport/login    /edu_admin/ykt_jumps2_xty_sso_code/
```

`/api/v3/user/login/app-web-*` 这一对是「取二维码 + 轮询确认」的形态，
理论上可以不用 WebView 实现原生扫码登录，**本次未验证**。

---

## 易踩的误判：`/v2/api/web/*` 的前缀级参数校验

探测端点时极易被误导。实测：

```
GET /v2/api/web/courses/list?identity=2     → 401 Cookie has no sessionid
GET /v2/api/web/announcement/unread-list    → 401   ← 曾据此误判「端点存在」
GET /v2/api/web/zzz-definitely-not-real     → 401   ← 假路径同样 401
```

带上课 `classroom_id` 后更隐蔽：

```
GET /v2/api/web/announcement/unread-list?classroom_id=26109238  → {"errcode":0,"data":{"3214":57484120}}
GET /v2/api/web/zzz-fake-control-99999?classroom_id=26109238    → {"errcode":0,"data":{"3214":57484120}}  ← 完全相同
```

**假路径与真路径返回一模一样的响应**，说明这是路径无关的兜底处理。

结论：**探测 `/v2/api/web/*` 时必须同时请求一个假路径作对照组**，否则 401/200 都不构成证据。
真正有鉴别力的是 `/mooc-api/*`（假路径返回 nginx 404）与 `/smart_education/*`。

---

## 8. 当前用户资料

> **2026-09-21 更新（Android 实测）**：`/api/v3/*` 在 Envoy 网关（`cube-api-gateway`）后面，
> 对 Java/Conscrypt 协议栈（.NET Android 的默认 `HttpClientHandler`）一律返回
> `{"code":50000,"msg":"UNAUTHENTICATED"}`；`/v/*` 系（如 `/v/course_meta/user_info`）
> 对同一协议栈返回 `web_redirect` 跳转。**两类问题在换用托管 `SocketsHttpHandler` 后都消失**
> （推测网关按 TLS/HTTP 指纹区分客户端）。现在资料接口主用 `/v/course_meta/user_info`，
> v3 仅作兜底；HTTP 栈统一为 SocketsHttpHandler。

```
GET /api/v3/user/basic-info
→ {"code":0,"msg":"OK","data":{
     "id":"59015896","name":"林晓健","nickname":"微信用户",
     "avatar":"http://qn-sx.yuketang.cn/tougao_pic_xxx_thumb_568.png",
     "school":"珠海科技学院","schoolNumber":"03251508","role":2}}
```

> **`nickname` 不可用**：微信登录不提供真实昵称，实测值为「微信用户」这类占位。
> 界面应使用 `name`（实名）。

同一数据的其他来源（字段形状不同，均可选）：

| 端点 | 结构 |
|---|---|
| `/v2/api/web/userinfo` | `data` 是**数组**：`[{"name":"…","user_id":…,"school_number":"…","avatar":"…"}]` |
| `/v/course_meta/user_info` | `data.user_profile.{name,nickname,school,avatar,phone_number}`（蛇形命名 `school_number`） |

**头像的两个注意点：**

1. 雨课堂返回的是 `http://`。同域名 https 可用，使用前应提升为 https。
2. 实际内容是 **JPEG**，尽管文件名为 `.png`。按内容解码即可，不受扩展名影响。

---

## 9. 网页端路由（构造「在浏览器中打开」链接用）

来源：前端路由表实测（`student-corpus.js` 里 `path:` / `name:` 的定义），
以及前端自身跳转时的调用方式。

| 路由 | name | 用途 |
|---|---|---|
| `/studentLog/:classroomid` | `studentLog-view` | 课程日志页（学生视角课程主页，作业/公告/课件入口） |
| `/noticeView/:classroomid/:noticeid` | `notice-view` | 单条公告详情 |
| `/noticeLinkList/:classroomid/:linkid` | `links-view` | 外部链接类公告 |
| `/exam/:cid/:eid` | `exam` | 考试 / 试卷类作业 |
| `/exercise/:classroomId/:leafId/:skuId` | `exercise` | 章节作业 |
| `/studentCards/:classroomid/:cardsid/:activityid` | `studentCards-view` | 课件 |

页面挂在 `/v2/web` 前缀下。

### ⚠ 不存在的路由（踩过的坑）

```
/v2/web/studentLog/{room}/homework        ✗  → 落到 /v2/web/errpage
/v2/web/studentLog/{room}/announcement    ✗  → 落到 /v2/web/errpage
```

`studentLog` 只接受一个 `:classroomid` 段，**后面不能再挂子路径**。
作业与公告是课程日志页里的页签，不是独立路径。

### 课程日志页的参数是必需的

前端自己跳转时带的查询参数：

```js
this.$router.push({
  name: "studentLog-view",
  params: { classroomid: a },
  query: { university_id: r, platform_id: o, classroom_id: a, content_url: "" },
});
```

其中 `platform_id` 默认取 `3`。缺这些参数时页面无法定位课程。可用形式：

```
https://changjiang.yuketang.cn/v2/web/studentLog/{room}?university_id=3214&platform_id=3&classroom_id={room}
```

### 注意：SPA 路由无法用 HTTP 状态码探测

所有前端路由都返回 `200` + 同一份 SPA 外壳 HTML（约 6 KB），
**包括 `/v2/web/errpage` 本身**。所以「GET 一下看是不是 200」完全无法判断路由是否存在，
必须查路由表，或在真实浏览器里看最终渲染结果。

### 深链的未验证部分

- **单条公告**：`/noticeView/{room}/{noticeid}` 需要 `noticeid`。学习日志里 type=9 的 `id`
  与通知中心的 `notify_id` 不是同一体系，没有实测确认该传哪个，因此当前实现不起用深链，
  统一回落到课程日志页。
- **作业深链**：`/exam/:cid/:eid` 的 `eid` 应传日志 id 还是试卷 id 未确认；
  `/exercise/:classroomId/:leafId/:skuId` 还需要 `sku_id`。
  因此当前实现也统一回落到课程日志页——作业就在该页的「未完成」页签下。
  这些 id 都已在响应里（`activity.id`、`leaf_id`、`content.sku_id`），
  要启用深链只需确认真实页面 URL 后调整 `Services/YktUrls.cs`。

---

## 实测基准（用于回归比对）

`tools/SyncSmoke` 在本账号上的正确输出，可作为改动后的回归基准：

```
课程 16 门 · 学习日志 51 条 · 作业 14 项 · 公告 8 条 · 通知中心未读 609

待完成 2 项
  [进行中] 2/22  09-17 10:20  2026-2027-1学期 大学物理BII第九章作业
  [未开始] 0/0   09-27 20:00  概率论与数理统计B作业 第2次

已完成 12 项（含成绩：课堂小测1—前测 100、课堂小测2-前测 100、
              线上小测验1 100、第1-2周作业-自测题1 0、作业 第1次 0）

未读公告 1 / 共 8
```

> 这个基准与上一轮会话人工核对过的结论一致：**未完成 2 项**、模电 6 项均为已完成、
> 「第2章 作业3」「第3章 作业1」已完成。
