// Keeps the GV bridge Chrome window BELOW Radio Console's kiosk after a (re)launch.
//
// On Wayland nothing outside the compositor can restack another client's window, and mutter
// raises and focuses every newly mapped window, so each bridge relaunch (watchdog, nightly
// restart, login autostart) landed on top of the kiosk. This extension lowers the bridge window
// as soon as it is shown, then hands focus back to the window now on top.
//
// Scope: a window is the bridge only if its owning process's command line carries the bridge
// profile marker (--user-data-dir=~/.config/gv-bridge-chrome), the same marker
// gv-bridge-ensure.sh uses. Chrome uses one app id for every profile, so the app id cannot tell
// the bridge from the kiosk. Every other window is ignored. LOWERED, never minimized: the bridge
// page must keep rendering (see docs/SETUP-GVBridge.md, "The bridge window is covered by the kiosk").
import GLib from 'gi://GLib';
import Meta from 'gi://Meta';
import {Extension} from 'resource:///org/gnome/shell/extensions/extension.js';

const MARKER = `user-data-dir=${GLib.get_home_dir()}/.config/gv-bridge-chrome`;

function isBridge(win) {
    const pid = win.get_pid();
    if (!pid || pid <= 0)
        return false;
    try {
        const [ok, bytes] = GLib.file_get_contents(`/proc/${pid}/cmdline`);
        // Chrome rewrites its process title: the args may be NUL- or space-separated.
        return ok && new TextDecoder().decode(bytes).replace(/\0/g, ' ').includes(MARKER);
    } catch (e) {
        return false;
    }
}

function sendBehind(win) {
    win.lower();
    const stack = global.display.sort_windows_by_stacking(
        global.display.get_tab_list(Meta.TabList.NORMAL_ALL, null));
    const top = stack.filter(w => w !== win && !w.minimized).pop();
    if (top)
        top.activate(global.get_current_time());
}

export default class GvBridgeBehindExtension extends Extension {
    enable() {
        this._createdId = global.display.connect('window-created', (_display, win) => {
            if (win.get_window_type() !== Meta.WindowType.NORMAL || !isBridge(win))
                return;
            // Lower once it is actually shown: lowering before mapping is undone by the
            // raise-on-map that mutter applies to a new window.
            const shownId = win.connect('shown', () => {
                win.disconnect(shownId);
                sendBehind(win);
            });
        });
    }

    disable() {
        if (this._createdId) {
            global.display.disconnect(this._createdId);
            this._createdId = null;
        }
    }
}
