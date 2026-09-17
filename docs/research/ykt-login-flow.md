# 长江雨课堂 (changjiang.yuketang.cn) 登录流程逆向笔记

- 调查对象：`D:\DS_Workpalce\ykt-probe\raw\student-corpus.js`（16,062,547 字节 / 15,652,144 字符，webpack 压缩 bundle）
- 调查方式：Node `indexOf` 逐词定位 + ±300~500 字符上下文摘录；**未发起任何网络请求**

## 0. 核心结论（TL;DR）

| 问题 | 结论 | 置信度 |
|---|---|---|
| 1. 微信扫码登录 endpoint 链 | **未找到**。该 bundle 内不存在扫码登录 API | 高（穷举 40+ 关键词，全部 0 命中） |
| 2. QR 图片获取方式 (a)/(b)/(c)/(d) | **本 bundle 内无法判定**。可排除 (c)：本 bundle 不含任何二维码生成库。登录 UI 由独立页面 `/login/weixinapp/` 或 iframe `/web?ykt_ai_login` 承担 | 中高 |
| 3. 轮询状态机 / session 建立 | **未找到** 轮询代码 | 高 |
| 4. 其他登录方式 | 短信绑定、Passport、SSO、iframe 登录均有 endpoint | 中（路径有，请求体未验证） |

**最重要的可执行结论**：该 bundle 只负责「检测未登录 → 交给外部登录页」，它自己不实现扫码。要实现纯 HttpClient 的微信扫码登录，**必须再抓取 `/login/weixinapp/` 页面的 HTML 及其专属 JS bundle**（本文件不含）。

## 1. 负结果证据（关键搜索词全部 0 命中）
```
wechat-auth-param 0   wxcode 0   wechat-web-callback 0   login/wechat 0   auth-param 0
check_login 0   login_status 0   wx_login 0   wechat_login 0   wxCode/wx_code 0
qr_login/qrlogin 0（仅 chunk 名，见 §5）  qrcode_url 0  qrcode_ticket 0  login_code 0
scan_login 0   scan_the_qr 0   qr_code 0   getqr/showqr 0
```

`qrcode` 仅 4 次命中，全部是 i18n 文案（`way_qrcode:"扫二维码"`、`menuqrcode:"二维码"`、`lessonqrcode:"课堂二维码"`、`get_chat_agent_qrcode:"/c27/.../se_share_outer/"`）。
`ticket` 16 次命中全部无关：ECharts 的 `this._ticket`（tooltip 回调去重）、发票 i18n `ticketNo:"工单号"`、微信 JS-SDK `startSearchBeacons:{ticket:e.ticket}`。

## 2. 登录入口（已确认）

bundle 里登录页面的**唯一入口是整页跳转 / iframe**，不是 API。原始 JS（offset ≈ 8,040,374，模块 73121）：
```js
try{if(window.self!==window.top)return void console.error("错误：登录逻辑应该 iframe 外部处理")}catch(e){console.log(e)}
if((0,f.S4)())return void window.dispatchEvent(new CustomEvent("yth_login_dialog",{detail:{isOpen:!0,bypassLoginCheck:!0}}));
if(_){var c=location.pathname;location.search&&(c+=location.search),location.href="/login/weixinapp/?next="+encodeURIComponent(c)}
else window.dispatchEvent(new CustomEvent("ykt_login_dialog",{detail:{isOpen:!0,bypassLoginCheck:!0}}));
```

其中 `_` 的定义（offset 8,039,318，同一模块 73121；模块 27541 的 `p` 定义完全相同）：
```js
var y=navigator.userAgent.toLowerCase(),_=y&&~y.indexOf("micromessenger")
```

**语义**：
- 在**微信内置浏览器**中 → 整页跳转 `/login/weixinapp/?next=<encodeURIComponent(pathname+search)>`
- 否则 → 派发 `ykt_login_dialog` 事件，由页面内登录弹窗处理（弹窗本体是 iframe，见 §6）
- 荷塘雨课堂平台（`f.S4()`，见 §4）→ 改用 `yth_login_dialog`

**置信度：高**（两个模块各自独立定义了同一个 micromessenger 判断）

401/403 拦截器同样走这条路（offset ≈ 8,054,873，模块 27541，`p` = 微信浏览器）：
```js
if(e.response&&403===e.response.status&&p){location.href="/login/weixinapp/?next="+location.href;return}
if(e.response&&401===e.response.status&&401999!==e.response.data.errcode||401===e.Status){if((0,d.IU)())return;
 -1!==location.href.indexOf("teacherLog")?location.href=location.origin+"/web?next="+location.pathname+"&type=3&share=1":
 p?location.href="/login/weixinapp/?next="+location.href:location.href=location.origin+"/web?next="+location.pathname+"&type=3";return}
```

**非微信环境下的兜底登录页是 `/web?next=<pathname>&type=3`**（PC 网页版登录页）；错误码 20009 也复用此逻辑。

## 3. 登录态探测 & 会话（C# 客户端可直接用）

### 3.1 轻量探测（推荐）— 仅路径/字段来自 JS，请求未验证
```js
// window.addEventListener("ykt_login_dialog", ...) 内
return u.trys.push([1,3,,4]),[4,s.Z.get("/api/open/is_login/")];
case 2: if((c=u.sent().data)&&c.is_login) return ...  // 已登录
```

- `GET /api/open/is_login/`；响应 `{ data: { is_login: <bool> } }`（JS 读 `resp.data.is_login`）
- **置信度：中高**（路径与字段名直引；未验证匿名请求是否返回 200、是否需额外头）

### 3.2 用户信息 — `/v2/api/web` 前缀已确认

模块 33221（offset 7,956,595 – 8,014,442）开头与同模块 `pc.index` 表：
```js
var o,a="/v2/api/web",s=window.location.hostname;n.Z={ ... }
index:{GET_USER_INFO:a+"/userinfo",GET_TEAM_INFO:"/api/v3/group/team/detail",GET_USER_ROLE:a+"/classrooms_role",
  GET_CLASS_LIST:a+"/courses/list",JOIN_CLASS2:a+"/classrooms/join_code", ... }
```

- `GET /v2/api/web/userinfo` → `errcode === 0` 表示已登录，数据 `data[0].user_id`
- 调用代码：`API.pc.index.GET_USER_INFO; request.get(n).then(n=>{n&&0===n.errcode&&commit({type:"SET_USER_INFO",data:n.data[0]})})`
- 另有 `GET /v2/api/web/classrooms_role`、`GET /v2/api/web/courses/list`
- **置信度：高**（前缀 `a` 与用法在同一模块内闭环引用）

### 3.3 退出 — `GET /pc/web_logout`
```js
case 0:return[4,request.get("/pc/web_logout")];   // 成功后清 localStorage.runtime_xtbz、sessionStorage.clear()
```
**置信度：高**

### 3.4 请求头 / Cookie（模块 73121 & 27541；offset ≈ 8,041,300 / 8,044,600 / 8,052,550）
```js
x.defaults.headers["X-CSRFToken"]=l().get("csrftoken")||""; x.defaults.withCredentials=!0;
x.defaults.headers["Xt-Agent"]=l().get("Xt-Agent")||"web";
e.headers.xtbz=(0,f.S4)()?(0,f.K3)():(0,p.ez)()?(0,p.Qq)():"ykt";
e.headers["uv-id"]=e.headers["university-id"]=...||l().get("uv-id")||l().get("uv_id")||l().get("university-id")||l().get("university_id")||0;
a().defaults.headers["X-Client"]="web";
window.Authorization&&(a().defaults.headers.Authorization="Bearer "+window.Authorization);
window.userid&&(a().defaults.headers["X-UID"]=window.userid);
(window.classroomId||localStorage.getItem("classroomId"))&&(a().defaults.headers["classroom-id"]=...);
```

- **认证主体是 Cookie 会话**（`withCredentials:!0`），JS **不读取** `sessionid`（全文 `jsessionid` 0 命中；`sessionid` 16 次命中全是 URL query 参数 `...&sessionid=`，用于打开课堂大屏，非登录票据）
- 建议请求头：`X-CSRFToken`(=cookie `csrftoken`)、`Xt-Agent: web`、`xtbz: ykt`、`university-id`/`uv-id: 0`、`X-Client: web`
- **置信度：高**（代码直引）

## 4. 平台标识 xtbz 判定（影响 changjiang）

模块 41245（offset 8,027,170）与模块 6210（offset 6,202,941）：
```js
// 6210: S4 = 是否荷塘雨课堂(yth)
function a(){try{let e=localStorage.getItem("runtime_xtbz");return"yth"===e||window.location.search.indexOf("isyth=1")>-1||window.location.search.indexOf("istraining=1")>-1||o()}catch(e){return!1}}
// 41245: Qq = 归一化 xtbz；ez = 是否 xt/ngsrp
function c(){var e=a().get("xtbz")||localStorage.getItem("runtime_xtbz")||"xt";return("ykt"===e||"cloud"===e||"training"===e)&&(e="xt"),e}
```

- cookie `xtbz` / localStorage `runtime_xtbz` 决定平台；缺省 → `"ykt"`
- 长江雨课堂预期取 **`ykt`**；若 cookie 为 `ykt`/`cloud`/`training`，部分接口会归一化为 `xt`
- **置信度：中**（逻辑确认；`changjiang` 具体取值未在 bundle 中硬编码验证）

## 5. `wx-qrlogin` chunk：存在但为空壳

模块 84981 的 `require.context` 映射表（offset 7,121,598）：
```js
var o={"./ai-correct-rule/lang/en.js":"16262", ... "./rule-paper/lang/zh_CN.js":"67472",
"./unfinished-study/lang/en.js":"55366","./unfinished-study/lang/zh_CN.js":"22139",
"./wx-qrlogin/lang/en.js":"96110","./wx-qrlogin/lang/zh_CN.js":"21096"};
```

但两个目标模块在**本 bundle 内是空对象**（offset 7,120,349 与 7,120,405）：
```js
96110:function(e,n,r){"use strict";r.r(n),n.default={}},21096:function(e,n,r){"use strict";r.r(n),n.default={}},
```

且 webpack chunk 名映射（offset 1,290）中**没有** `wx-qrlogin`：
```js
i.u=function(e){return"js/"+(({15344:"lang-en-js",23071:"correcting-assistant-evaluate",59393:"aicenter",6284:"lesson-prepare-assistant",63522:"lang-zh_CN-js",84301:"intelligent-accompany",94807:"ai-display-index",9726:"correcting-assistant"})[e]||e)+"."+({...})[e]+".js"}
```

**推论**：扫码登录组件被切到另一个 bundle（登录页专属编译产物），本文件只保留了空的 i18n 占位模块与模块引用表。
**置信度：高**（映射表 + 空模块定义双重直证）

## 6. 其他登录方式（已确认路径）

| 方式 | Method + Path | 依据 / 置信度 |
|---|---|---|
| 手机绑定/短信验证码 | `/pc/send_sms_code`（方法未直证） | `send_sms_code:"/pc/send_sms_code"`；路径高 / 方法低 |
| 是否已绑手机 | `/pc/is_bind_phone` | `is_bind_phone:"/pc/is_bind_phone"`；中 |
| 绑定手机 | `/pc/bind_phone` | `BIND_PHONE:"/pc/bind_phone"`；中 |
| Passport 登录/用户 | `/passport/login`、`/passport/userinfo`、`/passport/userid`、`/nodeapi/userinfo` | 直引；中（活动页专用表） |
| 学在清华 SSO（xty） | `GET /edu_admin/ykt_jumps2_xty_sso_code/?course_id=<id>` → 返回 `{code, xty_domain}`；再 `GET <xty_domain>/edu_admin/ykt_jumps2_xty_sso/?code=<code>&source=course_outline` | 直引（含参数）；高 |
| CMS AI 工作台 SSO | `GET /edu_admin/cms_ai_workspace_sso/?code=<code>`，成功写 sessionStorage 后跳转 | 直引；高 |
| AI 判题员登录 | `/c27/online_courseware/agent/member/<id>/judger_login/` | `x.get(l)`；高 |
| iframe 登录（荷塘 yth） | `/pro/iframe_login`（有 `?is_yth_h5=1` 变体）+ postMessage | 见下；高 |
| 密码 / 图形验证码 / 教务账号登录 | **未找到**（`captcha` 0 命中，无 password 类 endpoint） | — |

**iframe 登录实现（模块 41990 + 1158；offset 13,323,163 / 13,725,619）**：
```js
em="/pro/iframe_login",                                    // 模块级常量（荷塘 yth）
this.iframe.setAttribute("id","yth_login_iframe"),this.iframe.setAttribute("src",e?"".concat(em,"?is_yth_h5=1"):em),
var t=e.data&&e.data.action;"yth_login_success"===t?this.handleLoginSuccess():"yth_login_cancel"===t&&this.close()
this.yktLoginPath=e||"/web?ykt_ai_login"                    // 模块 1158：ykt 登录弹窗 iframe src
t.setAttribute("id","ykt_login_iframe"),t.setAttribute("src",this.yktLoginPath)
this.messageHandler=function(e){var a=e.data,s=e.origin;if("login_success"===a&&s===location.origin){ ... window.location.reload() }}
```

**关键旁证**：登录弹窗 iframe 尺寸硬编码 **416×510 px**（模块 1158 的 `createDialog`），符合「二维码 + 提示文案」登录框尺寸；但**本 bundle 不含其内部实现**。

## 7. 轮询状态机 — 未找到

- 全文无 `poll_*`、无 `check_login`、无扫码状态常量（`scanned`/`confirmed`/`expired` 组合）。
- 唯一与扫码状态相关的文案在**腾讯会议授权** i18n（offset 8,364,046，与本登录无关）：
  ```js
  authorize:{logining:"正在登录中...",loginsuc:"登录成功",loginerr:"登录失败<br/>点击重试",
    logingtimeout:"二维码已过期<br/>点击刷新",loginqrsuc:"扫码登录成功",loginqrerr:"获取二维码异常<br/>点击刷新",loginqrupdating:"二维码刷新中", ...}
  ```
- 微信 JS-SDK 的 `scanQRCode` 包装（offset 11,734,117）是**公众号内扫码结果解析**，非登录：
  ```js
  scanQRCode:function(e){k("scanQRCode",{needResult:(e=e||{}).needResult||0,scanType:e.scanType||["qrCode","barCode"]},
    (e._complete=function(e){var n;u&&(n=e.resultStr)&&(n=JSON.parse(n),e.resultStr=n&&n.scan_code&&n.scan_code.scan_result)},e))}
  ```

## 8. 「未验证 / 未找到」清单

1. **未找到**：微信扫码登录的 `get qrcode` / `poll` / `confirm` 任何 API 路径、方法、字段名 → 不在本 bundle。
2. **未验证**：`/api/open/is_login/` 是否需要 `Xt-Agent`/`xtbz` 等头；是否对匿名请求返回 200。
3. **未验证**：`/pc/send_sms_code`、`/pc/bind_phone` 的 HTTP 方法与请求体字段（表中只有路径）。
4. **未验证**：会话 cookie 的真实名称/属性 —— bundle 中不存在读取 `sessionid` cookie 的 JS，故 cookie 名未能从本文件证实。
5. **未找到**：密码登录、图形验证码、教务系统账号登录。
6. **无法判定**：QR 图片形态属于 (a) `<img src=PNG>` / (b) base64 JSON / (d) 跳转 open.weixin.qq.com —— 三者均无证据；**(c) 客户端生成已被排除**（无 `qrcode-generator`/`qrious`/`jquery-qrcode`，无 QR 相关 `toDataURL`）。
7. `/login/weixinapp/` 本身**不是 API**，是服务端渲染页面（bundle 中仅作为 `location.href` 目标出现 15 次）。

## 9. 对 C# 客户端实现的下一步建议

1. 用 `GET /api/open/is_login/`（或 `GET /v2/api/web/userinfo`，判 `errcode===0`）做登录态探测基座。
2. **另抓** `https://changjiang.yuketang.cn/login/weixinapp/` 的 HTML 及其 `<script src>` 指向的登录页 bundle —— 扫码 API 几乎必然在其中；再抓 `/web?ykt_ai_login`（iframe 登录页）作为第二候选。
3. 抓取时携带 `Xt-Agent: web`、`xtbz: ykt`、`university-id: 0`、`X-Client: web`，并持久化 Cookie 容器（`withCredentials` 语义）。
4. 该 bundle 的 401 分支表明：**登录成功靠整页 reload 生效**，即会话由服务端 Set-Cookie 建立；客户端只需在轮询到「已扫码确认」后保留 CookieContainer，无需处理回调跳转。
