// Live feed: subscribe to /api/stream (Server-Sent Events) and prepend each notification to the list.
// EventSource reconnects on its own if the connection drops, so there is no retry logic here.
(() => {
  const feed = document.getElementById("feed");
  const state = document.getElementById("live-state");
  const maxItems = 100;

  const source = new EventSource("/api/stream");
  source.onopen = () => { state.textContent = "live"; state.classList.add("on"); };
  source.onerror = () => { state.textContent = "reconnecting…"; state.classList.remove("on"); };

  source.onmessage = (message) => {
    const n = JSON.parse(message.data);
    const li = document.createElement("li");
    li.className = `new kind-${n.kind === "resolved" ? "resolved" : "firing"}`;

    const time = document.createElement("time");
    time.textContent = new Date(n.detectedAt).toISOString().replace("T", " ").slice(0, 19);
    const link = document.createElement("a");
    link.href = `/alerts/${encodeURIComponent(n.alertKey)}`;
    link.textContent = n.title; // textContent, never innerHTML: the payload is data, not markup
    const detail = document.createElement("div");
    detail.className = "sub";
    detail.textContent = n.detail;

    li.append(time, link, detail);
    feed.prepend(li);
    while (feed.children.length > maxItems) feed.lastElementChild.remove();
    scheduleStatsRefresh();
  };

  // Refresh the counters at most once a second, however many notifications arrive.
  let pending = false;
  function scheduleStatsRefresh() {
    if (pending) return;
    pending = true;
    setTimeout(async () => {
      pending = false;
      try {
        const stats = await (await fetch("/api/stats")).json();
        for (const el of document.querySelectorAll("[data-stat]")) {
          const value = stats[el.dataset.stat];
          if (value !== undefined) el.textContent = Number(value).toLocaleString("en-US");
        }
      } catch { /* the next notification will try again */ }
    }, 1000);
  }
})();
