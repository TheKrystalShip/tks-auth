package com.thekrystalship.shell;

import android.Manifest;
import android.content.pm.PackageManager;
import android.os.Build;
import android.webkit.JavascriptInterface;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

/**
 * What the page reaches of the shell, as {@code window.TksShell}. Every answer is a string, JSON
 * where it is a value, since that is what a bridge carries.
 *
 * <ul>
 *   <li>{@code app()}: {@code {name, version, shell, features}}, the app this build is, the shell's
 *       own version and the features it was built with; that the object is there at all is how a page
 *       knows it runs inside the shell.</li>
 *   <li>{@code download(json)}: takes a download over ({@link Downloads}); its URLs may be relative to
 *       the site, and every one has to be on it, so the headers the page hands over reach no other
 *       server. Answers {@code "ok"}, or why not.</li>
 *   <li>{@code status(id)}, {@code statuses()}, {@code pause(id)}, {@code resume(id)},
 *       {@code remove(id)}: the downloads, as {@link Downloads} says. An app built without the
 *       {@code downloads} feature takes none, and lists none.</li>
 *   <li>{@code fileUrl(id, name)}: where the page reads a whole downloaded file ({@link LocalFiles}).</li>
 *   <li>{@code theme(colour)}: the colour the page shows, which the strips behind the system bars take;
 *       the shell's own page script says it.</li>
 *   <li>{@code saveFile(name, type, base64)}: saves bytes the page made into the phone's Downloads
 *       ({@link SavedFiles}); the shell's own page script calls it for a {@code download} link to a
 *       {@code blob:} or {@code data:} address.</li>
 * </ul>
 *
 * Every call arrives on the WebView's bridge thread, never the page's. A WebView puts the bridge on
 * every page it loads, so each call answers only while the site's own page is showing
 * ({@link ShellActivity#onSite}): the sign-in's pages, on other origins, reach nothing.
 */
final class Bridge {
    /** The name the page finds the bridge under. */
    static final String NAME = "TksShell";
    /** The shell's own version, which moves when what a page can ask of it does. */
    static final String VERSION = "3";

    private final ShellActivity activity;
    private final Origin origin;
    private final Downloads downloads;
    private final SavedFiles saved;

    Bridge(ShellActivity activity, Origin origin, Downloads downloads, SavedFiles saved) {
        this.activity = activity;
        this.origin = origin;
        this.downloads = downloads;
        this.saved = saved;
    }

    @JavascriptInterface
    public String app() {
        JSONObject out = new JSONObject();
        try {
            out.put("name", activity.getString(R.string.app_name));
            out.put("version", Built.VERSION);
            out.put("shell", VERSION);
            out.put("features", new JSONArray(Built.FEATURES.isEmpty() ? new String[0] : Built.FEATURES.split(" ")));
        } catch (JSONException impossible) {
            throw new IllegalStateException(impossible);
        }
        return out.toString();
    }

    @JavascriptInterface
    public String download(String json) {
        if (!Built.DOWNLOADS) return "This app does not download.";
        if (!activity.onSite()) return "This page cannot download.";
        try {
            JSONObject download = new JSONObject(json);
            JSONArray files = download.getJSONArray("files");
            for (int i = 0; i < files.length(); i++) {
                JSONObject file = files.getJSONObject(i);
                String url = onSite(file.getString("url"));
                if (url == null) return "A file of this download is not on this site.";
                file.put("url", url);
            }
            JSONObject done = download.optJSONObject("done");
            if (done != null && done.has("url")) {
                String url = onSite(done.getString("url"));
                if (url == null) return "This download's end is not on this site.";
                done.put("url", url);
            }
            downloads.add(download);
        } catch (JSONException refused) {
            return refused.getMessage();
        }
        askToNotify();
        DownloadService.start(activity);
        return "ok";
    }

    @JavascriptInterface
    public String status(String id) {
        if (!activity.onSite()) return "null";
        JSONObject status = downloads.status(id);
        return status != null ? status.toString() : "null";
    }

    @JavascriptInterface
    public String statuses() {
        if (!activity.onSite()) return "{}";
        return downloads.statuses().toString();
    }

    @JavascriptInterface
    public void pause(String id) {
        if (activity.onSite()) downloads.pause(id);
    }

    @JavascriptInterface
    public void resume(String id) {
        if (!activity.onSite()) return;
        downloads.resume(id);
        DownloadService.start(activity);
    }

    @JavascriptInterface
    public void remove(String id) {
        if (activity.onSite()) downloads.remove(id);
    }

    /** Saves bytes the page made into the phone's Downloads under the page's name: "ok", or why not. */
    @JavascriptInterface
    public String saveFile(String name, String type, String base64) {
        if (!activity.onSite()) return "This page cannot save files.";
        try {
            saved.save(name, type, android.util.Base64.decode(base64, android.util.Base64.DEFAULT));
            return "ok";
        } catch (IllegalArgumentException | java.io.IOException failed) {
            return String.valueOf(failed.getMessage());
        }
    }

    /** The colour the page shows, as CSS gives it, which the strips behind the system bars take. */
    @JavascriptInterface
    public void theme(String colour) {
        if (!activity.onSite()) return;
        Integer parsed = parse(colour);
        if (parsed != null) activity.tone(parsed);
    }

    /** {@code rgb(r, g, b)}, {@code rgba(r, g, b, a)} or {@code #rrggbb}, as an opaque colour, or null. */
    static Integer parse(String colour) {
        if (colour == null) return null;
        String c = colour.trim();
        try {
            if (c.startsWith("#") && c.length() == 7) return 0xFF000000 | Integer.parseInt(c.substring(1), 16);
            java.util.regex.Matcher m = java.util.regex.Pattern.compile("rgba?\\(\\s*(\\d+)[ ,]+(\\d+)[ ,]+(\\d+)").matcher(c);
            if (m.find()) return 0xFF000000 | Integer.parseInt(m.group(1)) << 16 | Integer.parseInt(m.group(2)) << 8 | Integer.parseInt(m.group(3));
        } catch (NumberFormatException malformed) {
            return null;
        }
        return null;
    }

    @JavascriptInterface
    public String fileUrl(String id, String name) {
        return origin.origin() + LocalFiles.PREFIX + android.net.Uri.encode(id) + "/" + android.net.Uri.encode(name);
    }

    /** An address made absolute against the site, or null when it is not on the site. */
    private String onSite(String address) {
        String url = origin.resolve(address);
        return url != null && origin.owns(android.net.Uri.parse(url)) ? url : null;
    }

    /** The download notification needs leave to show from Android 13; asked once, with the first download. */
    private void askToNotify() {
        if (Build.VERSION.SDK_INT < 33) return;
        if (activity.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED) return;
        activity.runOnUiThread(new Runnable() {
            @Override
            public void run() {
                activity.requestPermissions(new String[] {Manifest.permission.POST_NOTIFICATIONS}, 1);
            }
        });
    }
}
