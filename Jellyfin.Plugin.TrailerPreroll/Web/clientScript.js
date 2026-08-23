/*
 * Trailer Preroll - "Want to Watch" button.
 * Injected into the Jellyfin web client. While a Trailer preroll is playing, shows a floating
 * button; clicking it asks the plugin to add the film to the user's "Watch Later" playlist.
 * Uses a fixed-position overlay (not the player's control bar) so it survives web-client updates.
 */
(function () {
    if (window.__tpWatchLater) {
        return;
    }

    window.__tpWatchLater = true;

    var BTN_ID = 'tpWatchLaterBtn';
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
    // true while PAUSED too, so the button stays put when the user pauses. Returns false once playback
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
        var btn = document.getElementById(BTN_ID);
        busy = true;
        if (btn) {
            btn.textContent = 'Adding…';
            btn.disabled = true;
        }

        var url = ac.getUrl('TrailerPreroll/WatchLater?itemId=' + encodeURIComponent(currentItem.Id));
        ac.ajax({ type: 'POST', url: url, dataType: 'json' }).then(function (res) {
            if (btn) { btn.textContent = '✓ Added to Watch Later'; }
            toast((res && res.title ? '"' + res.title + '"' : 'Added') + ' — saved to your Watch Later playlist.');
            reset(2500);
        }, function () {
            if (btn) { btn.textContent = 'Failed — try again'; }
            reset(2500);
        });
    }

    function reset(delay) {
        setTimeout(function () {
            busy = false;
            var btn = document.getElementById(BTN_ID);
            if (btn) {
                btn.textContent = '★ Want to watch';
                btn.disabled = false;
            }
        }, delay);
    }

    function showButton() {
        var btn = document.getElementById(BTN_ID);
        if (btn) {
            return;
        }

        btn = document.createElement('button');
        btn.id = BTN_ID;
        btn.type = 'button';
        btn.textContent = '★ Want to watch';
        btn.style.cssText = [
            'position:fixed', 'top:5.5em', 'right:1.5em', 'z-index:100000',
            'padding:0.7em 1.1em', 'border:none', 'border-radius:8px',
            'background:rgba(20,20,20,0.7)', 'color:#fff', 'font-size:1.05em',
            'font-weight:600', 'cursor:pointer', 'box-shadow:0 2px 10px rgba(0,0,0,0.5)',
            '-webkit-backdrop-filter:blur(6px)', 'backdrop-filter:blur(6px)'
        ].join(';');
        btn.addEventListener('click', addToWatchLater);
        btn.addEventListener('mouseenter', function () { btn.style.background = 'rgba(0,120,215,0.85)'; });
        btn.addEventListener('mouseleave', function () { btn.style.background = 'rgba(20,20,20,0.7)'; });
        document.body.appendChild(btn);
    }

    function hideButton(force) {
        var btn = document.getElementById(BTN_ID);
        if (btn && (force || !busy)) {
            btn.remove();
            if (force) {
                busy = false;
            }
        }
    }

    function tick() {
        if (!videoLoaded()) {
            hideButton();
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
                showButton();
            } else {
                hideButton();
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
                hideButton(true);
            }
        }, true);
    });

    setInterval(tick, POLL_MS);
})();
