// Aperture's web client. Plain JavaScript, no libraries. Every piece of text from the server
// (titles come from file names) is set with textContent, never parsed as HTML.
'use strict';

const app = document.getElementById('app');
let state = { server: '', library: null, bandwidth: null };
let leavePage = null; // cleanup for the current page (stops timers, saves progress)

// ---------- Small helpers ----------

function h(tag, props, ...children) {
  const el = document.createElement(tag);
  for (const [key, value] of Object.entries(props || {})) {
    if (value === null || value === undefined || value === false) continue;
    if (key === 'class') el.className = value;
    else if (key.startsWith('on')) el.addEventListener(key.slice(2), value);
    else if (key === 'text') el.textContent = value;
    else el.setAttribute(key, value === true ? '' : value);
  }
  for (const child of children.flat()) {
    if (child === null || child === undefined || child === false) continue;
    el.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return el;
}

const ICONS = {
  back: 'M15 5l-7 7 7 7',
  check: 'M5 12.5l4.5 4.5L19 7.5',
  play: 'M8 5.5v13l10.5-6.5z',
  signout: 'M14 4h4a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2h-4M10 16l-4-4 4-4M6 12h10',
  next: 'M9 5l7 7-7 7',
};

function icon(name, fill) {
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.setAttribute('viewBox', '0 0 24 24');
  svg.setAttribute('aria-hidden', 'true');
  const path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
  path.setAttribute('d', ICONS[name]);
  if (fill) { path.setAttribute('fill', 'currentColor'); }
  else {
    path.setAttribute('fill', 'none');
    path.setAttribute('stroke', 'currentColor');
    path.setAttribute('stroke-width', '1.8');
    path.setAttribute('stroke-linecap', 'round');
    path.setAttribute('stroke-linejoin', 'round');
  }
  svg.append(path);
  return svg;
}

function clock(seconds) {
  seconds = Math.max(0, Math.floor(seconds || 0));
  const hrs = Math.floor(seconds / 3600), min = Math.floor((seconds % 3600) / 60), sec = seconds % 60;
  const two = n => String(n).padStart(2, '0');
  return hrs > 0 ? `${hrs}:${two(min)}:${two(sec)}` : `${min}:${two(sec)}`;
}

function length(seconds) {
  if (!seconds) return null;
  if (seconds < 60) return `${Math.round(seconds)} s`;
  const min = Math.round(seconds / 60);
  if (min < 60) return `${min} min`;
  return `${Math.floor(min / 60)} h ${min % 60} min`;
}

function rate(bps) {
  if (bps >= 1e8) return `${Math.round(bps / 1e6)} Mbps`;
  return bps >= 1e6 ? `${(bps / 1e6).toFixed(1)} Mbps` : `${Math.round(bps / 1000)} kbps`;
}

class ApiError extends Error {
  constructor(status, message) { super(message); this.status = status; }
}

async function api(path, options = {}) {
  const init = { credentials: 'same-origin', headers: { 'Accept': 'application/json' }, ...options };
  if (options.body !== undefined) {
    init.headers['Content-Type'] = 'application/json';
    init.body = JSON.stringify(options.body);
  }
  if (init.method && init.method !== 'GET') init.headers['X-Aperture'] = '1';
  let response;
  try {
    response = await fetch(path, init);
  } catch {
    throw new ApiError(0, `Can’t reach ${state.server || 'the computer running Aperture'}. Check that it’s awake and on the same network.`);
  }
  if (response.status === 401 && !path.startsWith('/api/session')) {
    showSignIn();
    throw new ApiError(401, 'Sign in to continue.');
  }
  const data = response.status === 204 ? null : await response.json().catch(() => null);
  if (!response.ok) throw new ApiError(response.status, (data && data.error) || 'Something went wrong on the server.');
  return data;
}

function setPage(...children) {
  if (leavePage) { const leave = leavePage; leavePage = null; leave(); }
  app.replaceChildren(...children);
  window.scrollTo(0, 0);
}

function loadingPage() { setPage(h('div', { class: 'loading', role: 'progressbar', 'aria-label': 'Loading' })); }

function messagePage(title, text, action) {
  setPage(h('div', { class: 'page' }, h('div', { class: 'message' },
    h('h2', { text: title }), h('p', { text }), action || null)));
}

function bar(title, { back, extra } = {}) {
  return h('header', { class: 'bar' },
    back ? h('button', { class: 'icon-button', 'aria-label': 'Back', onclick: () => history.length > 1 ? history.back() : go('#/') }, icon('back')) : h('span', { class: 'mark', 'aria-hidden': 'true' }),
    h('h1', { text: title }),
    extra || null);
}

function go(hash) { location.hash = hash; }

// ---------- What this device plays ----------

function capabilities() {
  const v = document.createElement('video');
  const can = type => v.canPlayType(type) !== '';
  const video = [], audio = [];
  if (can('video/mp4; codecs="avc1.640028"')) video.push('h264');
  if (can('video/mp4; codecs="hvc1.1.6.L120.90"') || can('video/mp4; codecs="hev1.1.6.L120.90"')) video.push('hevc');
  if (can('video/webm; codecs="vp9"') || can('video/mp4; codecs="vp09.00.40.08"')) video.push('vp9');
  if (can('video/webm; codecs="vp8"')) video.push('vp8');
  if (can('video/mp4; codecs="av01.0.08M.08"')) video.push('av1');
  if (can('audio/mp4; codecs="mp4a.40.2"')) audio.push('aac');
  if (can('audio/mpeg')) audio.push('mp3');
  if (can('audio/webm; codecs="opus"') || can('audio/ogg; codecs="opus"') || can('audio/mp4; codecs="opus"')) audio.push('opus');
  if (can('audio/ogg; codecs="vorbis"') || can('audio/webm; codecs="vorbis"')) audio.push('vorbis');
  if (can('audio/flac') || can('audio/mp4; codecs="flac"')) audio.push('flac');
  if (can('audio/mp4; codecs="ac-3"')) audio.push('ac3');
  if (can('audio/mp4; codecs="ec-3"')) audio.push('eac3');
  if (can('audio/mp4; codecs="alac"')) audio.push('alac');
  return {
    video, audio,
    hls: can('application/vnd.apple.mpegurl') || can('application/x-mpegURL'),
    webm: can('video/webm'),
  };
}

// Times a download from the server, so a stream can be chosen that the connection carries.
async function measureBandwidth() {
  const cached = sessionStorage.getItem('aperture.bandwidth');
  if (cached) {
    const { bps, at } = JSON.parse(cached);
    if (Date.now() - at < 10 * 60 * 1000) return bps;
  }
  const time = async kb => {
    const started = performance.now();
    const response = await fetch(`/api/speedtest?kb=${kb}&t=${Date.now()}`, { credentials: 'same-origin', cache: 'no-store' });
    await response.arrayBuffer();
    return { bits: kb * 1024 * 8, seconds: (performance.now() - started) / 1000 };
  };
  try {
    let sample = await time(512);
    if (sample.seconds < 0.5) sample = await time(4096);
    const bps = Math.round(sample.bits / Math.max(sample.seconds, 0.01));
    try { sessionStorage.setItem('aperture.bandwidth', JSON.stringify({ bps, at: Date.now() })); } catch { /* private mode */ }
    return bps;
  } catch {
    return null;
  }
}

function deviceName() {
  const ua = navigator.userAgent;
  const device = /iPad/.test(ua) || (/Macintosh/.test(ua) && navigator.maxTouchPoints > 1) ? 'iPad'
    : /iPhone/.test(ua) ? 'iPhone'
    : /Android/.test(ua) ? (/Mobile/.test(ua) ? 'Android phone' : 'Android tablet')
    : /Windows/.test(ua) ? 'Windows PC' : /Macintosh/.test(ua) ? 'Mac' : /Linux/.test(ua) ? 'Linux computer' : 'Browser';
  const browser = /EdgA?\//.test(ua) ? 'Edge' : /SamsungBrowser/.test(ua) ? 'Samsung Internet' : /Firefox|FxiOS/.test(ua) ? 'Firefox'
    : /CriOS|Chrome/.test(ua) ? 'Chrome' : /Safari/.test(ua) ? 'Safari' : null;
  return browser ? `${device} · ${browser}` : device;
}

// ---------- Sign in ----------

function showSignIn(message) {
  const input = h('input', { class: 'text', id: 'passphrase', type: 'password', autocomplete: 'current-password', required: true, autofocus: true });
  const error = h('div', { class: 'error', role: 'alert', hidden: !message, text: message || '' });
  const submit = h('button', { class: 'button primary', type: 'submit', text: 'Sign in' });
  const form = h('form', {
    onsubmit: async event => {
      event.preventDefault();
      submit.disabled = true;
      error.hidden = true;
      try {
        await api('/api/session', { method: 'POST', body: { passphrase: input.value, deviceName: deviceName() } });
        route();
      } catch (e) {
        error.textContent = e.message;
        error.hidden = false;
        input.select();
      } finally {
        submit.disabled = false;
      }
    },
  },
  h('label', { class: 'field', for: 'passphrase', text: 'Passphrase' }), input, error, submit);
  setPage(h('div', { class: 'page' }, h('section', { class: 'signin' },
    h('span', { class: 'mark', 'aria-hidden': 'true' }),
    h('h1', { text: state.server ? `Aperture on ${state.server}` : 'Aperture' }),
    h('p', { text: 'Enter the passphrase you set in Aperture’s settings on that computer.' }),
    form,
    h('p', { class: 'hint', text: 'This device stays signed in for 30 days after you last use it. You can sign it out from the computer at any time.' }))));
  input.focus();
}

// ---------- Cards ----------

function artwork(card, { progress = true } = {}) {
  const art = h('div', { class: 'art' }, h('div', { class: 'placeholder' }));
  const img = h('img', { alt: '', loading: 'lazy', decoding: 'async', src: `/api/items/${card.id}/image` });
  img.addEventListener('error', () => img.remove());
  art.append(img);
  if (progress && card.progress > 0 && !card.watched) {
    const fill = h('span');
    fill.style.width = `${Math.max(3, Math.round(card.progress * 100))}%`; // CSSOM, which the page's CSP allows
    art.append(h('div', { class: 'progress', 'aria-hidden': 'true' }, fill));
  }
  if (card.watched) art.append(h('span', { class: 'watched', title: 'Watched' }, icon('check')));
  return art;
}

function cardMeta(card, { remaining } = {}) {
  const parts = [];
  if (card.note) parts.push(h('span', { class: 'note', text: card.note }));
  if (card.subtitle) parts.push(card.subtitle);
  if (remaining && card.resumeSeconds > 0 && card.durationSeconds) parts.push(`${length(card.durationSeconds - card.resumeSeconds)} left`);
  else if (card.durationSeconds) parts.push(length(card.durationSeconds));
  const meta = h('div', { class: 'card-meta' });
  parts.forEach((part, i) => { if (i) meta.append(' · '); meta.append(part); });
  return meta;
}

function itemCard(card, options = {}) {
  return h('a', { class: 'card', href: `#/play/${card.id}`, 'aria-label': [card.title, card.subtitle].filter(Boolean).join(', ') },
    artwork(card),
    h('div', { class: 'card-title', text: card.title }),
    cardMeta(card, options));
}

function showCard(show) {
  return h('a', { class: 'card', href: `#/show/${show.id}`, 'aria-label': show.title },
    artwork({ id: show.imageItemId, progress: 0 }, { progress: false }),
    h('div', { class: 'card-title', text: show.title }),
    h('div', { class: 'card-meta', text: show.note || '' }));
}

const SECTIONS = [
  { key: 'movies', title: 'Movies', route: 'movies' },
  { key: 'shows', title: 'TV shows', route: 'shows' },
  { key: 'personal', title: 'Personal videos', route: 'videos' },
  { key: 'podcasts', title: 'Podcasts', route: 'podcasts' },
];

// ---------- Home ----------

async function showHome() {
  loadingPage();
  const library = state.library = await api('/api/library');
  const signOut = h('button', { class: 'icon-button', 'aria-label': 'Sign out', title: 'Sign out', onclick: signOutNow }, icon('signout'));
  const page = h('div', { class: 'page' }, bar(state.server ? `Aperture on ${state.server}` : 'Aperture', { extra: signOut }));

  const total = library.movies.length + library.shows.length + library.personal.length + library.podcasts.length;
  if (total === 0) {
    page.append(h('div', { class: 'message' },
      h('h2', { text: library.sharedFolders === 0 ? 'Nothing is shared yet' : 'No videos in the shared folders' }),
      h('p', { text: library.sharedFolders === 0
        ? `On ${state.server || 'the computer'}, open Aperture › Settings › Media server and choose which library folders to share.`
        : 'Aperture hasn’t found videos in the shared folders yet. Scan the library on the computer, then come back.' }),
      h('button', { class: 'button', text: 'Check again', onclick: route })));
    setPage(page);
    return;
  }

  if (library.continue.length) {
    page.append(h('section', { class: 'section' },
      h('div', { class: 'section-head' }, h('h2', { text: 'Continue watching' })),
      h('div', { class: 'rail' }, library.continue.map(c => itemCard(c, { remaining: true })))));
  }
  for (const section of SECTIONS) {
    const list = library[section.key];
    if (!list.length) continue;
    const many = list.length > 8;
    page.append(h('section', { class: 'section' },
      h('div', { class: 'section-head' },
        h('h2', { text: section.title }),
        many ? h('a', { href: `#/${section.route}`, text: `All ${list.length}` }) : null),
      h('div', { class: 'rail compact' }, list.slice(0, 12).map(x => section.key === 'shows' ? showCard(x) : itemCard(x)))));
  }
  setPage(page);
}

async function showCategory(route) {
  const section = SECTIONS.find(s => s.route === route);
  if (!section) return go('#/');
  if (!state.library) { loadingPage(); state.library = await api('/api/library'); }
  const list = state.library[section.key];
  setPage(h('div', { class: 'page' }, bar(section.title, { back: true }),
    h('div', { class: 'grid' }, list.map(x => section.key === 'shows' ? showCard(x) : itemCard(x)))));
}

// ---------- A show ----------

async function showShow(id) {
  loadingPage();
  let show;
  try { show = await api(`/api/shows/${encodeURIComponent(id)}`); }
  catch (e) { return messagePage('This show isn’t shared', e.message, h('a', { class: 'button', href: '#/', text: 'Back to library' })); }

  const list = h('ul', { class: 'episodes' });
  const tabs = h('div', { class: 'seasons', role: 'tablist' });
  const select = season => {
    for (const tab of tabs.children) tab.setAttribute('aria-selected', String(tab.dataset.season === String(season.number)));
    list.replaceChildren(...season.episodes.map(e => h('li', { class: 'episode', onclick: () => go(`#/play/${e.id}`) },
      artwork(e),
      h('div', {},
        h('a', { class: 'card-title', href: `#/play/${e.id}`, text: e.subtitle || e.title }),
        cardMeta({ ...e, subtitle: null, note: null })))));
  };
  if (show.seasons.length > 1) {
    show.seasons.forEach(season => tabs.append(h('button', { role: 'tab', 'data-season': season.number, text: season.label, onclick: () => select(season) })));
  }
  const nextSeason = show.next && show.seasons.find(s => s.episodes.some(e => e.id === show.next.id));
  setPage(h('div', { class: 'page' },
    bar(show.title, { back: true }),
    h('div', { class: 'show-head' },
      show.next ? h('a', { class: 'button primary', href: `#/play/${show.next.id}` }, icon('play', true),
        `${show.next.note === 'Continue' ? 'Continue' : 'Play'} ${show.next.subtitle ? show.next.subtitle.split(' · ')[0] : ''}`.trim()) : null),
    show.seasons.length > 1 ? tabs : null,
    list));
  select(nextSeason || show.seasons[0]);
}

// ---------- Player ----------

async function showPlayer(id) {
  loadingPage();
  let item;
  try { item = await api(`/api/items/${id}`); }
  catch (e) { return messagePage('This video isn’t shared', e.message, h('a', { class: 'button', href: '#/', text: 'Back to library' })); }
  const card = item.card;
  const caps = capabilities();
  const bandwidthPromise = state.bandwidth ? Promise.resolve(state.bandwidth) : measureBandwidth().then(b => (state.bandwidth = b));

  const video = h('video', { controls: true, playsinline: true, 'webkit-playsinline': true, preload: 'metadata', 'aria-label': card.title });
  const poster = h('img', { class: 'poster', alt: '', src: `/api/items/${card.id}/image` });
  poster.addEventListener('error', () => poster.remove());
  const status = h('div', { class: 'status', hidden: true });
  const wrap = h('div', { class: 'video-wrap' }, poster, video, status);
  const resumeLine = h('div', { class: 'actions' });
  const warning = h('div', { class: 'notice-line', hidden: true });
  const facts = h('dl', { class: 'facts' });
  const diagnostics = h('details', { class: 'diagnostics' }, h('summary', { text: 'Playback details' }), facts);

  let plan = null;          // the server's answer
  let offset = 0;           // where a converted stream starts in the file
  let triedFallback = false;
  let lastSaved = 0;
  let streamInfo = null;
  let bandwidth = null;

  const position = () => offset + (video.currentTime || 0);
  const duration = () => card.durationSeconds || (plan && plan.mode === 'direct' ? video.duration : null) || null;

  async function saveProgress(force) {
    if (!plan || plan.mode === 'unavailable') return;
    const now = Date.now();
    if (!force && now - lastSaved < 10000) return;
    if (position() < 1) return;
    lastSaved = now;
    try {
      await fetch(`/api/items/${card.id}/progress`, {
        method: 'POST', keepalive: true, credentials: 'same-origin',
        headers: { 'Content-Type': 'application/json', 'X-Aperture': '1' },
        body: JSON.stringify({ position: position(), duration: Number.isFinite(duration()) ? duration() : null }),
      });
    } catch { /* the next save tries again */ }
  }

  function showStatus(text, button) {
    status.replaceChildren(h('div', {}, h('p', { text }), button || null));
    status.hidden = false;
  }

  async function load(startSeconds, avoidDirectPlay) {
    status.hidden = true;
    bandwidth = await Promise.race([bandwidthPromise, new Promise(r => setTimeout(() => r(null), 4000))]);
    try {
      plan = await api(`/api/items/${card.id}/play`, { method: 'POST', body: { ...caps, bandwidthBps: bandwidth, startSeconds, avoidDirectPlay } });
    } catch (e) {
      showStatus(e.message, h('button', { class: 'button', text: 'Try again', onclick: () => load(startSeconds, avoidDirectPlay) }));
      return;
    }
    renderFacts();
    warning.hidden = !plan.warning;
    warning.textContent = plan.warning || '';
    if (plan.mode === 'unavailable') {
      video.removeAttribute('src');
      video.hidden = true;
      showStatus(plan.reason);
      return;
    }
    video.hidden = false;
    offset = plan.mode === 'direct' ? 0 : plan.startSeconds;
    video.src = plan.url;
    if (plan.mode === 'direct' && plan.startSeconds > 0) {
      video.addEventListener('loadedmetadata', () => { video.currentTime = plan.startSeconds; }, { once: true });
    }
    video.addEventListener('loadeddata', () => poster.remove(), { once: true });
  }

  function renderResume() {
    resumeLine.replaceChildren();
    if (card.resumeSeconds > 0) {
      resumeLine.append(
        h('span', { class: 'meta', text: `Resumes at ${clock(card.resumeSeconds)}` }),
        h('button', { class: 'link-button', text: 'Start from the beginning', onclick: () => {
          card.resumeSeconds = 0;
          renderResume();
          if (plan && plan.mode === 'direct') { video.currentTime = 0; }
          else { stopStream(); load(0, triedFallback); }
        } }));
    }
    if (item.nextId) {
      resumeLine.append(h('a', { class: 'button', href: `#/play/${item.nextId}` }, 'Next episode', icon('next')));
    }
  }

  function stopStream() {
    if (plan && plan.streamId) {
      fetch(`/media/${plan.streamId}`, { method: 'DELETE', keepalive: true, credentials: 'same-origin', headers: { 'X-Aperture': '1' } }).catch(() => {});
    }
  }

  function renderFacts() {
    const rows = [];
    const modeName = { direct: 'Direct play', remux: 'Repackaged', convert: 'Converted', unavailable: 'Can’t play here' }[plan.mode];
    rows.push(['How', modeName], ['Why', plan.reason]);
    if (plan.source) rows.push(['Original', plan.source]);
    if (plan.stream) rows.push(['Sent as', plan.stream]);
    if (plan.converter) rows.push(['Conversion', plan.converter]);
    if (streamInfo) rows.push(['Converting at', streamInfo.error ? streamInfo.error : `${streamInfo.speed.toFixed(1)}× real time${streamInfo.finished ? ', finished' : `, ${clock(streamInfo.produced)} ready`}`]);
    rows.push(['Connection', bandwidth ? `${rate(bandwidth)} measured` : 'Not measured']);
    if (video.videoWidth) rows.push(['Showing', `${video.videoWidth}×${video.videoHeight}`]);
    if (video.buffered.length) {
      let ahead = 0;
      for (let i = 0; i < video.buffered.length; i++) {
        if (video.buffered.start(i) <= video.currentTime && video.buffered.end(i) >= video.currentTime) ahead = video.buffered.end(i) - video.currentTime;
      }
      rows.push(['Loaded ahead', `${Math.round(ahead)} s`]);
    }
    const quality = video.getVideoPlaybackQuality ? video.getVideoPlaybackQuality() : null;
    if (quality && quality.totalVideoFrames) rows.push(['Dropped frames', `${quality.droppedVideoFrames} of ${quality.totalVideoFrames}`]);
    facts.replaceChildren(...rows.flatMap(([k, v]) => [h('dt', { text: k }), h('dd', { text: v })]));
  }

  video.addEventListener('timeupdate', () => saveProgress(false));
  video.addEventListener('pause', () => saveProgress(true));
  video.addEventListener('ended', () => {
    saveProgress(true);
    if (item.nextId) showStatus('Finished.', h('a', { class: 'button primary', href: `#/play/${item.nextId}` }, icon('play', true), 'Play the next episode'));
  });
  video.addEventListener('error', () => {
    if (!plan || !video.getAttribute('src')) return;
    if (plan.mode === 'direct' && !triedFallback) {
      // The browser said it could play the file but couldn't: ask for a converted stream from here.
      triedFallback = true;
      load(position(), true);
      return;
    }
    showStatus('This video stopped playing. The computer may have gone to sleep or left the network.',
      h('button', { class: 'button', text: 'Try again', onclick: () => load(position(), triedFallback) }));
  });

  const onHide = () => { if (document.visibilityState === 'hidden') saveProgress(true); };
  document.addEventListener('visibilitychange', onHide);
  window.addEventListener('pagehide', onHide);
  const timer = setInterval(async () => {
    if (diagnostics.open && plan) {
      if (plan.streamId) {
        try { streamInfo = await api(`/api/streams/${plan.streamId}`); } catch { streamInfo = null; }
      }
      renderFacts();
    }
  }, 2000);
  leavePage = () => {
    clearInterval(timer);
    document.removeEventListener('visibilitychange', onHide);
    window.removeEventListener('pagehide', onHide);
    saveProgress(true);
    video.pause();
    stopStream();
    video.removeAttribute('src');
    video.load();
  };

  const meta = [card.durationSeconds ? length(card.durationSeconds) : null, card.quality].filter(Boolean).join(' · ');
  setPage(h('div', { class: 'page player-page' },
    bar(item.showTitle || '', { back: true }),
    wrap,
    h('div', { class: 'details' },
      h('h1', { text: item.showTitle ? (card.subtitle || card.title) : card.title }),
      !item.showTitle && card.subtitle ? h('div', { class: 'sub', text: card.subtitle }) : null,
      meta ? h('div', { class: 'meta', text: meta }) : null,
      resumeLine,
      warning,
      item.overview ? h('p', { class: 'overview', text: item.overview }) : null,
      diagnostics)));
  renderResume();
  await load(card.resumeSeconds || 0, false);
}

async function signOutNow() {
  try { await api('/api/session', { method: 'DELETE' }); } catch { /* signed out either way */ }
  state.library = null;
  showSignIn();
}

// ---------- Routing ----------

async function route() {
  const hash = location.hash.replace(/^#\/?/, '');
  const [page, id] = hash.split('/');
  try {
    if (page === 'show' && id) await showShow(id);
    else if (page === 'play' && /^\d+$/.test(id || '')) await showPlayer(id);
    else if (['movies', 'shows', 'videos', 'podcasts'].includes(page)) await showCategory(page);
    else await showHome();
  } catch (e) {
    if (e.status === 401) return;
    messagePage('Aperture can’t load this', e.message, h('button', { class: 'button', text: 'Try again', onclick: route }));
  }
}

async function start() {
  loadingPage();
  let session;
  try { session = await api('/api/session'); }
  catch (e) { return messagePage('Aperture can’t connect', e.message, h('button', { class: 'button', text: 'Try again', onclick: start })); }
  state.server = session.server;
  document.title = `Aperture on ${session.server}`;
  if (!session.hasPassphrase) {
    return messagePage('Almost ready', `Set a passphrase in Aperture › Settings › Media server on ${session.server}, then come back.`,
      h('button', { class: 'button', text: 'Check again', onclick: start }));
  }
  if (!session.signedIn) return showSignIn();
  route();
}

window.addEventListener('hashchange', route);

start();
