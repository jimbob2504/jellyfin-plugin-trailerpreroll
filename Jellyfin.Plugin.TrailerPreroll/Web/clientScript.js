/*
 * Trailer Preroll - web player buttons.
 * Injected into the Jellyfin web client. While a Trailer preroll is playing, shows two floating
 * buttons:
 *   - "Want to watch": adds the film to the user's Watch Later playlist.
 *   - "Change trailer": removes this trailer (a replacement is downloaded server-side) and skips to
 *     the next preroll.
 * Uses fixed-position overlays (not the player's control bar) so they survive web-client updates.
 */
(function () {
    if (window.__tpWatchLater) {
        return;
    }

    window.__tpWatchLater = true;

    var WATCH_ID = 'tpWatchLaterBtn';
    var CHANGE_ID = 'tpChangeBtn';
    var POLL_MS = 1500;
    var DEBUG = false;
    var currentItem = null;
    var busy = false;
    var lastLogged = '';

    function log() {
        if (DEBUG && window.console) {
            var args = ['[TrailerPreroll]'].concat([].slice.call(arguments));
            console.log.apply(console, args);
        }
    }

    log('client script loaded');

    function api() {
        return window.ApiClient;
    }

    // A video is "loaded" if the player element exists, is still in the DOM, and has media data -
    // true while PAUSED too, so the buttons stay put when the user pauses. Returns false once playback
    // is closed/ended (element removed, disconnected, or ended).
    function videoLoaded() {
        var v = document.querySelector('video');
        return !!(v && v.isConnected && !v.ended && v.readyState >= 2);
    }

    function fetchNowPlaying(cb) {
        var ac = api();
        if (!ac || typeof ac.getSessions !== 'function') {
            log('no ApiClient.getSessions available');
            return cb(null);
        }

        var deviceId = typeof ac.deviceId === 'function' ? ac.deviceId() : null;
        ac.getSessions({}).then(function (sessions) {
            var mine = null;
            var anyPlaying = null;
            for (var i = 0; i < sessions.length; i++) {
                var s = sessions[i];
                if (!s.NowPlayingItem) {
                    continue;
                }

                anyPlaying = s.NowPlayingItem;
                if (deviceId && s.DeviceId === deviceId) {
                    mine = s.NowPlayingItem;
                    break;
                }
            }

            // Prefer this device's session; fall back to any session that is playing something.
            cb(mine || anyPlaying);
        }).catch(function (err) {
            log('getSessions failed', err);
            cb(null);
        });
    }

    function toast(msg) {
        try {
            if (window.Dashboard && typeof window.Dashboard.alert === 'function') {
                window.Dashboard.alert(msg);
            }
        } catch (e) { /* ignore */ }
    }

    function addToWatchLater() {
        if (!currentItem || busy) {
            return;
        }

        var ac = api();
        var btn = document.getElementById(WATCH_ID);
        busy = true;
        if (btn) {
            btn.textContent = 'Adding…';
            btn.disabled = true;
        }

        var url = ac.getUrl('TrailerPreroll/WatchLater?itemId=' + encodeURIComponent(currentItem.Id));
        ac.ajax({ type: 'POST', url: url, dataType: 'json' }).then(function (res) {
            var already = res && res.alreadyPresent;
            var title = res && res.title ? '"' + res.title + '"' : 'This';
            if (btn) { btn.textContent = already ? '✓ Already saved' : '✓ Added to Watch Later'; }
            toast(already
                ? title + ' is already in your Watch Later playlist.'
                : title + ' — saved to your Watch Later playlist.');
            resetButtons(2500);
        }, function () {
            if (btn) { btn.textContent = 'Failed — try again'; }
            resetButtons(2500);
        });
    }

    // Jump the current trailer to its end so Jellyfin advances to the next preroll (or the feature).
    function skipCurrent() {
        var v = document.querySelector('video');
        if (!v) {
            return;
        }

        try {
            if (isFinite(v.duration) && v.duration > 0) {
                v.currentTime = Math.max(0, v.duration - 0.05);
            } else {
                v.dispatchEvent(new Event('ended'));
            }
        } catch (e) {
            log('skip failed', e);
        }
    }

    function changeTrailer() {
        if (!currentItem || busy) {
            return;
        }

        var ac = api();
        var btn = document.getElementById(CHANGE_ID);
        busy = true;
        if (btn) {
            btn.textContent = 'Changing…';
            btn.disabled = true;
        }

        var url = ac.getUrl('TrailerPreroll/ReplaceTrailer?itemId=' + encodeURIComponent(currentItem.Id));
        ac.ajax({ type: 'POST', url: url, dataType: 'json' }).then(function () {
            // The server removes this trailer and downloads a different one in the background; skip now.
            currentItem = null;
            lastLogged = '';
            hideButtons(true);
            skipCurrent();
        }, function () {
            if (btn) { btn.textContent = 'Failed — try again'; }
            resetButtons(2500);
        });
    }

    function resetButtons(delay) {
        setTimeout(function () {
            busy = false;
            var w = document.getElementById(WATCH_ID);
            if (w) { w.textContent = '★ Want to watch'; w.disabled = false; }
            var c = document.getElementById(CHANGE_ID);
            if (c) { c.textContent = '⟳ Change trailer'; c.disabled = false; }
        }, delay);
    }

    function makeButton(id, label, top, onClick) {
        var btn = document.createElement('button');
        btn.id = id;
        btn.type = 'button';
        btn.textContent = label;
        btn.style.cssText = [
            'position:fixed', 'top:' + top, 'right:1.5em', 'z-index:100000',
            'padding:0.7em 1.1em', 'border:none', 'border-radius:8px',
            'background:rgba(20,20,20,0.7)', 'color:#fff', 'font-size:1.05em',
            'font-weight:600', 'cursor:pointer', 'box-shadow:0 2px 10px rgba(0,0,0,0.5)',
            '-webkit-backdrop-filter:blur(6px)', 'backdrop-filter:blur(6px)'
        ].join(';');
        btn.addEventListener('click', onClick);
        btn.addEventListener('mouseenter', function () { btn.style.background = 'rgba(0,120,215,0.85)'; });
        btn.addEventListener('mouseleave', function () { btn.style.background = 'rgba(20,20,20,0.7)'; });
        document.body.appendChild(btn);
        return btn;
    }

    function showButtons() {
        if (!document.getElementById(WATCH_ID)) {
            makeButton(WATCH_ID, '★ Want to watch', '5.5em', addToWatchLater);
        }

        if (!document.getElementById(CHANGE_ID)) {
            makeButton(CHANGE_ID, '⟳ Change trailer', '9em', changeTrailer);
        }
    }

    function hideButtons(force) {
        if (!force && busy) {
            return;
        }

        [WATCH_ID, CHANGE_ID].forEach(function (id) {
            var btn = document.getElementById(id);
            if (btn) { btn.remove(); }
        });

        if (force) {
            busy = false;
        }
    }

    function tick() {
        if (!videoLoaded()) {
            hideButtons();
            return;
        }

        fetchNowPlaying(function (item) {
            var sig = item ? (item.Type + ':' + item.Name) : 'none';
            if (sig !== lastLogged) {
                lastLogged = sig;
                log('video playing; nowPlaying =', item ? { Id: item.Id, Type: item.Type, Name: item.Name } : null);
            }

            if (item && item.Type === 'Trailer') {
                currentItem = item;
                showButtons();
            } else {
                hideButtons();
            }
        });
    }

    // Hide immediately when the player actually stops/closes, instead of waiting for the next poll or
    // for the server session to catch up. Capture phase catches events from any <video> element.
    ['ended', 'emptied', 'abort'].forEach(function (evt) {
        document.addEventListener(evt, function (e) {
            if (e.target && e.target.tagName === 'VIDEO') {
                currentItem = null;
                lastLogged = '';
                hideButtons(true);
            }
        }, true);
    });

    setInterval(tick, POLL_MS);
})();
