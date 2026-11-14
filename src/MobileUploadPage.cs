namespace RepairAlert;

/// <summary>Страница загрузки для телефона (Safari на iPhone, Chrome на Android).
/// Полностью встроена в программу: без внешних библиотек и без интернета.</summary>
internal static class MobileUploadPage
{
    private const string Head = """
        <!DOCTYPE html>
        <html lang="ru">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
        <meta name="color-scheme" content="light dark">
        <meta name="robots" content="noindex">
        <title>СЕРВИС ЛЕГКО — файлы с телефона</title>
        <style>
        :root{--bg:#f5f5f7;--card:#fff;--ink:#1d1d1f;--muted:#6e6e73;--line:#e5e5ea;--accent:#0071e3;--ok:#34c759;--bad:#ff3b30;--track:#e5e5ea}
        @media (prefers-color-scheme:dark){:root{--bg:#000;--card:#1c1c1e;--ink:#f5f5f7;--muted:#98989d;--line:#38383a;--accent:#0a84ff;--ok:#30d158;--bad:#ff453a;--track:#38383a}}
        *{box-sizing:border-box}
        [hidden]{display:none!important}
        body{margin:0;background:var(--bg);color:var(--ink);font:16px/1.4 -apple-system,BlinkMacSystemFont,"Segoe UI",sans-serif;-webkit-text-size-adjust:100%}
        header{background:#111;color:#fff;padding:calc(18px + env(safe-area-inset-top)) 20px 18px}
        header h1{margin:0;font-size:22px;letter-spacing:.5px}
        header p{margin:4px 0 0;color:#c7c7cc;font-size:14px}
        main{padding:16px 16px calc(24px + env(safe-area-inset-bottom));max-width:640px;margin:0 auto}
        .card{background:var(--card);border-radius:14px;padding:16px;margin-bottom:14px}
        .muted{color:var(--muted);font-size:14px}
        .btn{display:block;width:100%;border:0;border-radius:12px;padding:15px;font-size:17px;font-weight:600;cursor:pointer;-webkit-appearance:none}
        .primary{background:var(--accent);color:#fff}
        .secondary{background:var(--track);color:var(--ink)}
        .btn:disabled{opacity:.45}
        input[type=file]{display:none}
        ul{list-style:none;margin:0;padding:0}
        li{display:flex;gap:12px;align-items:center;padding:10px 0;border-top:1px solid var(--line)}
        li:first-child{border-top:0}
        .thumb{width:52px;height:52px;border-radius:8px;object-fit:cover;background:var(--track);flex:none;display:flex;align-items:center;justify-content:center;font-size:12px;color:var(--muted);overflow:hidden}
        .info{flex:1;min-width:0}
        .name{white-space:nowrap;overflow:hidden;text-overflow:ellipsis;font-size:15px}
        .meta{font-size:13px;color:var(--muted)}
        .bar{height:5px;background:var(--track);border-radius:3px;margin-top:6px;overflow:hidden}
        .bar i{display:block;height:100%;width:0;background:var(--accent);transition:width .2s}
        .done .bar i{background:var(--ok)}
        .err .meta{color:var(--bad)}
        .x{border:0;background:none;color:var(--muted);font-size:20px;padding:6px 8px;cursor:pointer}
        .total{display:flex;justify-content:space-between;font-size:14px;color:var(--muted);margin:10px 0 0}
        #status{font-size:15px;margin:10px 0 0;min-height:21px}
        #status.ok{color:var(--ok)} #status.bad{color:var(--bad)}
        .gap{height:10px}
        </style>
        </head>
        """;

    public const string Html = Head + """
        <body>
        <header><h1>СЕРВИС ЛЕГКО</h1><p id="ctx">Подключение к компьютеру…</p></header>
        <main>
          <div class="card">
            <label class="btn primary" id="pickBtn" for="picker">Выбрать фото, видео или файлы</label>
            <input type="file" id="picker" multiple>
            <p class="muted" style="margin:10px 0 0">До 5 ГБ на каждый файл. Можно выбрать из «Фото» или «Файлов», несколько сразу. Файлы передаются без изменений.</p>
          </div>
          <div class="card" id="listCard" hidden>
            <ul id="list"></ul>
            <div class="total"><span id="totalCount"></span><span id="totalSize"></span></div>
            <div class="bar" id="totalBarWrap" hidden><i id="totalBar"></i></div>
            <div id="status"></div>
            <div class="gap"></div>
            <button class="btn primary" id="sendBtn">Отправить</button>
            <div class="gap"></div>
            <button class="btn secondary" id="moreBtn" hidden>Выбрать ещё</button>
          </div>
          <p class="muted" id="hint">Не закрывайте эту страницу и не блокируйте телефон, пока идёт передача больших файлов. Если передача прервалась — откройте страницу снова и выберите те же файлы: уже переданные части повторно не отправляются.</p>
        </main>
        <script>
        (function(){
        "use strict";
        var token = location.pathname.split("/").filter(Boolean).pop();
        var api = "/api/m/" + encodeURIComponent(token);
        var MAX = 5 * 1024 * 1024 * 1024, CHUNK = 8 * 1024 * 1024, PARALLEL = 2;
        var items = [], sending = false, expired = false;
        var $ = function(id){ return document.getElementById(id); };

        function fmt(n){
          if (n < 1024) return n + " Б";
          var u = ["КБ","МБ","ГБ"], i = -1;
          do { n /= 1024; i++; } while (n >= 1024 && i < 2);
          return (n >= 100 ? n.toFixed(0) : n.toFixed(1)).replace(".", ",") + " " + u[i];
        }
        function setStatus(text, cls){ var s = $("status"); s.textContent = text || ""; s.className = cls || ""; }

        var CRC_TABLE = (function(){ var t = new Uint32Array(256);
          for (var i = 0; i < 256; i++){ var c = i; for (var k = 0; k < 8; k++) c = (c & 1) ? (0xEDB88320 ^ (c >>> 1)) : (c >>> 1); t[i] = c >>> 0; }
          return t; })();
        function crc32(bytes){ var c = 0xFFFFFFFF;
          for (var i = 0; i < bytes.length; i++) c = CRC_TABLE[(c ^ bytes[i]) & 0xFF] ^ (c >>> 8);
          return ((c ^ 0xFFFFFFFF) >>> 0).toString(16).padStart(8, "0"); }

        function request(method, url, body, headers, onProgress){
          return new Promise(function(resolve, reject){
            var x = new XMLHttpRequest();
            x.open(method, url, true);
            x.timeout = 120000;
            if (headers) for (var h in headers) x.setRequestHeader(h, headers[h]);
            if (onProgress && x.upload) x.upload.onprogress = function(e){ onProgress(e.loaded); };
            x.onload = function(){ var data = null; try { data = JSON.parse(x.responseText); } catch(e){}
              resolve({ status: x.status, data: data || {} }); };
            x.onerror = function(){ reject(new Error("network")); };
            x.ontimeout = function(){ reject(new Error("timeout")); };
            x.send(body === undefined ? null : body);
          });
        }
        function sleep(ms){ return new Promise(function(r){ setTimeout(r, ms); }); }
        var BACKOFF = [1000, 2000, 5000, 10000, 30000];

        function goneMessage(){ expired = true; sending = false;
          setStatus("QR-код устарел или окно на компьютере закрыто. Откройте QR-код снова и отсканируйте его.", "bad");
          $("sendBtn").disabled = true; }

        function render(){
          var list = $("list"); list.innerHTML = "";
          $("listCard").hidden = items.length === 0;
          var total = 0, done = 0;
          items.forEach(function(it, idx){
            total += it.file.size; done += it.done ? it.file.size : Math.min(it.file.size, it.sent + it.inflight);
            var li = document.createElement("li");
            if (it.done) li.className = "done"; else if (it.error) li.className = "err";
            var ext = (it.file.name.split(".").pop() || "").slice(0, 5).toUpperCase();
            var th = document.createElement(it.thumb ? "img" : "div"); th.className = "thumb";
            if (it.thumb){
              th.src = it.thumb;
              // браузер не умеет показать этот формат (например, HEIC на Android) — показываем расширение
              th.onerror = function(){ if (it.thumb) URL.revokeObjectURL(it.thumb); it.thumb = null; render(); };
            } else th.textContent = ext;
            var info = document.createElement("div"); info.className = "info";
            var nm = document.createElement("div"); nm.className = "name"; nm.textContent = it.file.name;
            var meta = document.createElement("div"); meta.className = "meta";
            meta.textContent = it.error ? it.error : it.done ? "✓ Отправлено · " + fmt(it.file.size)
              : it.started ? fmt(Math.min(it.file.size, it.sent + it.inflight)) + " из " + fmt(it.file.size) + (it.note ? " · " + it.note : "")
              : fmt(it.file.size);
            var bar = document.createElement("div"); bar.className = "bar";
            var fill = document.createElement("i");
            fill.style.width = (it.done ? 100 : it.file.size ? Math.floor(100 * Math.min(it.file.size, it.sent + it.inflight) / it.file.size) : 0) + "%";
            bar.appendChild(fill);
            info.appendChild(nm); info.appendChild(meta); if (it.started || it.done) info.appendChild(bar);
            li.appendChild(th); li.appendChild(info);
            if (!sending && !it.done){
              var x = document.createElement("button"); x.className = "x"; x.textContent = "✕"; x.setAttribute("aria-label", "Убрать");
              x.onclick = function(){ if (it.thumb) URL.revokeObjectURL(it.thumb); items.splice(idx, 1); render(); };
              li.appendChild(x);
            }
            list.appendChild(li);
          });
          var left = items.filter(function(it){ return !it.done; }).length;
          $("totalCount").textContent = items.length + " " + (items.length === 1 ? "файл" : items.length < 5 ? "файла" : "файлов");
          $("totalSize").textContent = fmt(total);
          $("totalBarWrap").hidden = !sending && done === 0;
          $("totalBar").style.width = (total ? Math.floor(100 * done / total) : 0) + "%";
          $("sendBtn").hidden = left === 0;
          $("sendBtn").disabled = sending || expired;
          $("sendBtn").textContent = sending ? "Отправка…" : "Отправить";
          $("moreBtn").hidden = sending || left > 0;
          $("pickBtn").style.display = sending ? "none" : "";
        }

        $("picker").addEventListener("change", function(){
          var tooBig = [];
          Array.prototype.forEach.call(this.files, function(f){
            if (f.size > MAX){ tooBig.push(f.name); return; }
            var dup = items.some(function(it){ return !it.done && it.file.name === f.name && it.file.size === f.size && it.file.lastModified === f.lastModified; });
            if (dup) return;
            var thumb = /^image\/(jpeg|png|gif|webp|heic|heif)$/i.test(f.type) ? URL.createObjectURL(f) : null;
            items.push({ file: f, thumb: thumb, sent: 0, inflight: 0, done: false, started: false, error: "", note: "" });
          });
          this.value = "";
          setStatus(tooBig.length ? "Больше 5 ГБ, не добавлены: " + tooBig.join(", ") : "", tooBig.length ? "bad" : "");
          render();
        });
        $("moreBtn").addEventListener("click", function(){ $("picker").click(); });

        async function uploadFile(it){
          var f = it.file;
          it.started = true; it.error = ""; it.note = ""; render();
          var key = f.name + "|" + f.size + "|" + f.lastModified;
          var r = await withRetry(function(){ return request("POST", api + "/files", JSON.stringify({ name: f.name, size: f.size, key: key }),
            { "Content-Type": "application/json" }); }, it);
          if (r.status === 410) { goneMessage(); return; }
          if (r.status !== 200){ it.error = r.data.error || "Компьютер не принял файл"; return; }
          var info = r.data;
          if (info.completed){ it.done = true; return; }
          var chunk = info.chunkSize || CHUNK, count = info.chunkCount;
          var got = {}; (info.received || []).forEach(function(i){ got[i] = true; });
          var queue = [];
          for (var i = 0; i < count; i++) if (!got[i]) queue.push(i);
          it.sent = 0; for (var j in got) it.sent += Math.min(chunk, f.size - j * chunk);
          render();
          var current = {};
          async function worker(){
            while (queue.length && !expired){
              var idx = queue.shift();
              var start = idx * chunk, end = Math.min(f.size, start + chunk);
              var buf = new Uint8Array(await f.slice(start, end).arrayBuffer());
              var crc = crc32(buf), attempts = 0;
              while (true){
                current[idx] = 0;
                var res = await withRetry(function(){ return request("PUT", api + "/files/" + info.id + "/chunks/" + idx, buf,
                  { "Content-Type": "application/octet-stream", "X-Crc32": crc },
                  function(loaded){ current[idx] = loaded; it.inflight = sum(current); render(); }); }, it);
                delete current[idx]; it.inflight = sum(current);
                if (res.status === 200){ it.sent += buf.length; render(); break; }
                if (res.status === 410){ goneMessage(); return; }
                if (res.status === 422 && ++attempts < 5) continue;
                throw new Error((res.data && res.data.error) || "Часть файла не принята");
              }
            }
          }
          await Promise.all(Array.from({ length: Math.min(PARALLEL, Math.max(1, queue.length)) }, worker));
          if (expired) return;
          it.note = "проверка и сохранение на компьютере…"; render();
          var c = await withRetry(function(){ return request("POST", api + "/files/" + info.id + "/complete", ""); }, it);
          while (true){
            if (c.status === 410){ goneMessage(); return; }
            if (c.status === 409 && c.data.missing){ return uploadFile(it); }
            if (c.status !== 200){ it.error = c.data.error || "Компьютер не принял файл"; return; }
            if (c.data.completed){ it.done = true; it.note = ""; return; }
            if (c.data.error){ it.error = c.data.error; return; }
            await sleep(1500);
            c = await withRetry(function(){ return request("GET", api + "/files/" + info.id); }, it);
            if (c.status === 200 && !c.data.completed && !c.data.processing && !c.data.error){
              // Обработка не запущена (например, компьютер перезапустил её) — попросить снова
              c = await withRetry(function(){ return request("POST", api + "/files/" + info.id + "/complete", ""); }, it);
            }
          }
        }
        function sum(o){ var s = 0; for (var k in o) s += o[k]; return s; }

        async function withRetry(fn, it){
          for (var attempt = 0; ; attempt++){
            try {
              var r = await fn();
              if (attempt > 0){ it.note = ""; setStatus("Связь восстановлена, продолжаем…"); }
              return r;
            } catch(e){
              if (expired) throw e;
              it.note = document.hidden ? "пауза" : "нет связи с компьютером, повторяем…";
              setStatus(document.hidden ? "" : "Нет связи с компьютером. Проверьте, что телефон в той же Wi‑Fi сети. Повторяем…", "bad");
              render();
              await sleep(BACKOFF[Math.min(attempt, BACKOFF.length - 1)]);
            }
          }
        }

        $("sendBtn").addEventListener("click", async function(){
          if (sending || expired) return;
          sending = true; setStatus("Отправка…"); render();
          try {
            for (var i = 0; i < items.length && !expired; i++){
              var it = items[i];
              if (it.done) continue;
              try { await uploadFile(it); } catch(e){ it.error = e.message || "Ошибка передачи"; }
              render();
            }
          } finally { sending = false; }
          if (expired) { render(); return; }
          var failed = items.filter(function(it){ return !it.done; }).length;
          setStatus(failed ? "Не все файлы отправлены. Нажмите «Отправить», чтобы повторить." : "✓ Все файлы получены компьютером", failed ? "bad" : "ok");
          render();
        });

        document.addEventListener("visibilitychange", function(){
          if (document.hidden && sending) setStatus("Передача на паузе: iPhone останавливает её, когда страница не на экране.", "bad");
          else if (!document.hidden && sending) setStatus("Продолжаем отправку…");
        });

        request("GET", api + "/status").then(function(r){
          if (r.status === 410 || r.status === 404){ goneMessage(); $("ctx").textContent = "Ссылка недействительна"; return; }
          $("ctx").textContent = r.data.title ? "Куда: " + r.data.title : "Подключено к компьютеру";
          if (r.data.maxFileBytes) MAX = r.data.maxFileBytes;
          if (r.data.chunkBytes) CHUNK = r.data.chunkBytes;
        }, function(){ $("ctx").textContent = "Нет связи с компьютером"; setStatus("Проверьте, что телефон в той же Wi‑Fi сети, что и компьютер.", "bad"); });
        render();
        })();
        </script>
        </body>
        </html>
        """;

    public const string ExpiredHtml = Head + """
        <body>
        <header><h1>СЕРВИС ЛЕГКО</h1><p>Файлы с телефона</p></header>
        <main>
          <div class="card">
            <p style="margin:0 0 8px;font-weight:600">Ссылка недействительна</p>
            <p class="muted" style="margin:0">QR-код устарел или окно загрузки на компьютере уже закрыто. Нажмите кнопку загрузки с телефона в программе ещё раз и отсканируйте новый QR-код.</p>
          </div>
        </main>
        </body>
        </html>
        """;
}
