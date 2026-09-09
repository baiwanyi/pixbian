/**
 * 内嵌 Web 前端资源（M7）。
 * 职责：提供浏览器端的响应式界面——登录、网格浏览、缩略图加载与视频播放。
 * 复用约定：HTML、CSS、JS 三个资源分开返回，配合 CSP 的 script-src 'self'，
 *          页面内不允许任何内联脚本（防 XSS 落地）；
 *          JSON 数据写入 DOM 只用 textContent，杜绝 innerHTML 注入面。
 * 关键约束：所有用户数据（文件名）展示前必须经 textContent 写入，绝不拼接 HTML 字符串；
 *          视频缩略图不支持，列表直接用 video 元素加载元数据帧；
 *          翻页用 hasMore 标记，滚动到底自动加载。
 */

using Pixbian.WebServer.Http;

namespace Pixbian.WebServer;

/// <summary>内嵌 Web 前端资源。</summary>
public static class WebAssets
{
    /// <summary>JSON 序列化选项（小驼峰，供前端直接消费）。</summary>
    public static System.Text.Json.JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
    };

    /// <summary>主页 HTML。</summary>
    public static HttpResponse Index() => HttpResponse.Html(Html);

    /// <summary>样式表。</summary>
    public static HttpResponse Stylesheet() => new()
    {
        StatusCode = 200,
        ContentType = "text/css; charset=utf-8",
        Body = System.Text.Encoding.UTF8.GetBytes(Css)
    };

    /// <summary>脚本。</summary>
    public static HttpResponse Script() => new()
    {
        StatusCode = 200,
        ContentType = "text/javascript; charset=utf-8",
        Body = System.Text.Encoding.UTF8.GetBytes(JavaScript)
    };

    private const string Html = """
        <!DOCTYPE html>
        <html lang="zh-CN">
        <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Pixbian</title>
            <link rel="stylesheet" href="/app.css">
        </head>
        <body>
            <header>
                <h1>Pixbian</h1>
                <div class="tools">
                    <select id="kindFilter">
                        <option value="">全部</option>
                        <option value="image">图片</option>
                        <option value="video">视频</option>
                    </select>
                    <input id="searchBox" type="search" placeholder="搜索文件名" autocomplete="off">
                    <button id="logoutButton" type="button">登出</button>
                </div>
            </header>

            <main id="grid" class="grid"></main>
            <div id="loading" class="loading hidden">加载中…</div>
            <div id="empty" class="empty hidden">媒体库为空，请先在桌面端添加文件夹并索引。</div>

            <div id="loginOverlay" class="overlay hidden">
                <form id="loginForm" class="login-card">
                    <h2>需要密码</h2>
                    <input id="passwordBox" type="password" placeholder="请输入访问密码" autocomplete="current-password">
                    <button type="submit">进入</button>
                    <p id="loginError" class="error hidden"></p>
                </form>
            </div>

            <div id="viewer" class="overlay hidden">
                <button id="viewerClose" class="viewer-close" type="button">×</button>
                <div id="viewerContent" class="viewer-content"></div>
                <div class="viewer-bar">
                    <span id="viewerTitle"></span>
                    <button id="favoriteButton" type="button">♥ 收藏</button>
                </div>
            </div>

            <script src="/app.js"></script>
        </body>
        </html>
        """;

    private const string Css = """
        :root { color-scheme: dark; }
        * { box-sizing: border-box; }
        body { margin: 0; font-family: "Segoe UI", system-ui, sans-serif; background: #1a1a1a; color: #eee; }
        header {
            display: flex; align-items: center; justify-content: space-between;
            gap: 12px; padding: 12px 16px; background: #242424;
            position: sticky; top: 0; z-index: 5;
        }
        header h1 { margin: 0; font-size: 18px; }
        .tools { display: flex; gap: 8px; }
        input, select, button {
            background: #333; color: #eee; border: 1px solid #444;
            border-radius: 6px; padding: 6px 10px; font: inherit;
        }
        button { cursor: pointer; }
        button:hover { background: #3d3d3d; }
        .grid {
            display: grid; gap: 8px; padding: 12px;
            grid-template-columns: repeat(auto-fill, minmax(140px, 1fr));
        }
        .tile {
            position: relative; aspect-ratio: 1; background: #2a2a2a;
            border-radius: 6px; overflow: hidden; cursor: pointer; border: none; padding: 0;
        }
        .tile img, .tile video { width: 100%; height: 100%; object-fit: cover; display: block; }
        .tile.video::after {
            content: ""; position: absolute; inset: 0;
            background: linear-gradient(135deg, #333 0%, #222 100%);
        }
        .tile.video .play {
            position: absolute; inset: 0; display: flex; align-items: center; justify-content: center;
            font-size: 30px; color: #ddd; pointer-events: none;
        }
        .tile .name {
            position: absolute; left: 0; right: 0; bottom: 0;
            padding: 4px 6px; font-size: 11px; color: #ddd;
            background: rgba(0,0,0,.55); white-space: nowrap;
            overflow: hidden; text-overflow: ellipsis; text-align: left;
        }
        .tile .badge {
            position: absolute; top: 6px; right: 6px; font-size: 12px;
            background: rgba(0,0,0,.55); border-radius: 4px; padding: 2px 5px;
        }
        .loading, .empty { text-align: center; padding: 32px; color: #888; }
        .hidden { display: none !important; }
        .overlay {
            position: fixed; inset: 0; background: rgba(0,0,0,.82);
            display: flex; align-items: center; justify-content: center; z-index: 10;
        }
        .login-card {
            display: flex; flex-direction: column; gap: 12px;
            padding: 24px; background: #242424; border-radius: 10px; min-width: 280px;
        }
        .login-card h2 { margin: 0 0 4px; font-size: 16px; }
        .error { color: #f66; margin: 0; font-size: 13px; }
        .viewer-content {
            max-width: 96vw; max-height: 86vh; display: flex; align-items: center; justify-content: center;
        }
        .viewer-content img, .viewer-content video { max-width: 96vw; max-height: 86vh; border-radius: 6px; }
        .viewer-bar {
            position: fixed; left: 0; right: 0; bottom: 0;
            display: flex; align-items: center; justify-content: space-between;
            padding: 10px 16px; background: rgba(20,20,20,.92);
        }
        .viewer-close {
            position: fixed; top: 12px; right: 16px; font-size: 22px;
            width: 40px; height: 40px; border-radius: 50%;
        }
        """;

    private const string JavaScript = """
        (function () {
            "use strict";

            const grid = document.getElementById("grid");
            const loading = document.getElementById("loading");
            const empty = document.getElementById("empty");
            const loginOverlay = document.getElementById("loginOverlay");
            const loginForm = document.getElementById("loginForm");
            const passwordBox = document.getElementById("passwordBox");
            const loginError = document.getElementById("loginError");
            const kindFilter = document.getElementById("kindFilter");
            const searchBox = document.getElementById("searchBox");
            const logoutButton = document.getElementById("logoutButton");
            const viewer = document.getElementById("viewer");
            const viewerContent = document.getElementById("viewerContent");
            const viewerTitle = document.getElementById("viewerTitle");
            const viewerClose = document.getElementById("viewerClose");
            const favoriteButton = document.getElementById("favoriteButton");

            let offset = 0;
            const limit = 60;
            let hasMore = true;
            let loadingPage = false;
            let currentId = null;

            async function fetchJson(url, options) {
                const response = await fetch(url, options);
                if (response.status === 401) {
                    showLogin();
                    throw new Error("unauthorized");
                }
                if (!response.ok) {
                    throw new Error("http " + response.status);
                }
                return response.json();
            }

            function showLogin() {
                loginOverlay.classList.remove("hidden");
            }

            function hideLogin() {
                loginOverlay.classList.add("hidden");
                loginError.classList.add("hidden");
            }

            loginForm.addEventListener("submit", async function (event) {
                event.preventDefault();
                try {
                    const response = await fetch("/api/login", {
                        method: "POST",
                        headers: { "Content-Type": "application/json" },
                        body: JSON.stringify({ password: passwordBox.value })
                    });
                    if (!response.ok) {
                        const data = await response.json();
                        loginError.textContent = data.error || "登录失败";
                        loginError.classList.remove("hidden");
                        return;
                    }
                    passwordBox.value = "";
                    hideLogin();
                    resetAndLoad();
                } catch (error) {
                    loginError.textContent = "网络错误，请重试";
                    loginError.classList.remove("hidden");
                }
            });

            function buildTile(item) {
                const tile = document.createElement("button");
                tile.className = "tile";
                tile.type = "button";

                if (item.kind === "video") {
                    // 网格不放内嵌 video 元素：每个 video 都是一次 /media 元数据请求，
                    // 数十个格子会把移动端带宽与 DOM 压力打满；占位样式 + 点击后播放即可。
                    tile.className += " video";
                    const play = document.createElement("span");
                    play.className = "play";
                    play.textContent = "▶";
                    tile.appendChild(play);
                } else {
                    const image = document.createElement("img");
                    image.loading = "lazy";
                    image.src = "/thumb/" + item.id;
                    image.alt = "";
                    tile.appendChild(image);
                }

                if (item.isFavorite) {
                    const badge = document.createElement("span");
                    badge.className = "badge";
                    badge.textContent = "♥";
                    tile.appendChild(badge);
                }

                const name = document.createElement("span");
                name.className = "name";
                name.textContent = item.fileName;
                tile.appendChild(name);

                tile.addEventListener("click", function () { openViewer(item); });
                return tile;
            }

            async function loadPage() {
                if (loadingPage || !hasMore) { return; }

                loadingPage = true;
                loading.classList.remove("hidden");

                try {
                    const params = new URLSearchParams({
                        offset: String(offset),
                        limit: String(limit)
                    });
                    if (kindFilter.value) { params.set("kind", kindFilter.value); }
                    if (searchBox.value.trim()) { params.set("q", searchBox.value.trim()); }

                    const data = await fetchJson("/api/items?" + params.toString());

                    data.items.forEach(function (item) {
                        grid.appendChild(buildTile(item));
                    });

                    offset += data.items.length;
                    hasMore = data.hasMore;
                    empty.classList.toggle("hidden", offset > 0);
                } catch (error) {
                    if (error.message !== "unauthorized") {
                        empty.textContent = "加载失败，请刷新重试。";
                        empty.classList.remove("hidden");
                    }
                } finally {
                    loading.classList.add("hidden");
                    loadingPage = false;
                }
            }

            function resetAndLoad() {
                offset = 0;
                hasMore = true;
                grid.replaceChildren();
                empty.classList.add("hidden");
                loadPage();
            }

            function openViewer(item) {
                currentId = item.id;
                viewerContent.replaceChildren();

                if (item.kind === "video") {
                    const video = document.createElement("video");
                    video.controls = true;
                    video.autoplay = true;
                    video.src = "/media/" + item.id;
                    viewerContent.appendChild(video);
                } else {
                    // 两级加载：先 320px 缩略图垫场（与桌面端查看器同一策略），
                    // 原图就绪后才替换——移动端不再对着黑屏等完整原图下载。
                    const preview = document.createElement("img");
                    preview.src = "/thumb/" + item.id;
                    preview.alt = item.fileName;
                    viewerContent.appendChild(preview);

                    const full = document.createElement("img");
                    full.alt = "";
                    full.addEventListener("load", function () {
                        // 守卫：用户在原图下载期间关闭查看器或切换条目时不得替换。
                        if (currentId === item.id) { viewerContent.replaceChildren(full); }
                    });
                    full.src = "/media/" + item.id;
                    viewerContent.appendChild(full);
                }

                viewerTitle.textContent = item.fileName;
                viewer.classList.remove("hidden");
            }

            function closeViewer() {
                viewerContent.replaceChildren();
                viewer.classList.add("hidden");
                currentId = null;
            }

            viewerClose.addEventListener("click", closeViewer);
            viewer.addEventListener("click", function (event) {
                if (event.target === viewer) { closeViewer(); }
            });

            kindFilter.addEventListener("change", resetAndLoad);

            logoutButton.addEventListener("click", async function () {
                try {
                    await fetch("/api/logout", { method: "POST" });
                } catch (error) {
                    // 请求失败也照常进入登录态：登出是本地的展示语义，令牌过期由服务端兜底。
                }
                resetAndLoad();
                showLogin();
            });

            let searchTimer = null;
            searchBox.addEventListener("input", function () {
                clearTimeout(searchTimer);
                searchTimer = setTimeout(resetAndLoad, 400);
            });

            window.addEventListener("scroll", function () {
                if (window.innerHeight + window.scrollY >= document.body.offsetHeight - 400) {
                    loadPage();
                }
            });

            fetchJson("/api/items?limit=1").then(function () {
                resetAndLoad();
            }).catch(function (error) {
                if (error.message !== "unauthorized") {
                    empty.textContent = "无法连接服务。";
                    empty.classList.remove("hidden");
                }
            });
        })();
        """;
}
