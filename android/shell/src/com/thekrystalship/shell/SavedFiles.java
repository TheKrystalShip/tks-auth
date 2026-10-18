package com.thekrystalship.shell;

import android.app.DownloadManager;
import android.content.ContentResolver;
import android.content.ContentValues;
import android.content.Context;
import android.net.Uri;
import android.os.Build;
import android.os.Environment;
import android.provider.MediaStore;
import android.webkit.CookieManager;
import android.webkit.URLUtil;
import android.widget.Toast;

import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.OutputStream;

/**
 * Files a page saves, as a browser saves them: into the phone's Downloads, under the name the page
 * gave, said with a short message. A file the page made itself ({@code blob:} or {@code data:}, which
 * nothing outside the page can read) arrives as its bytes from the page's own script
 * ({@code assets/page.js}, through {@link Bridge}); a file at an address is handed to the system's
 * download manager with the page's cookies, as a browser's own download would carry them.
 */
final class SavedFiles {
    private final Context context;

    SavedFiles(Context context) {
        this.context = context.getApplicationContext();
    }

    /** Saves bytes the page made under its name; answers where they went, or throws. */
    String save(String name, String type, byte[] bytes) throws IOException {
        String file = safe(name);
        String mime = typeOf(file, type);
        if (Build.VERSION.SDK_INT >= 29) {
            ContentResolver resolver = context.getContentResolver();
            ContentValues values = new ContentValues();
            values.put(MediaStore.Downloads.DISPLAY_NAME, file);
            values.put(MediaStore.Downloads.MIME_TYPE, mime);
            values.put(MediaStore.Downloads.IS_PENDING, 1);
            Uri uri = resolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values);
            if (uri == null) throw new IOException("Downloads would not take " + file);
            try (OutputStream out = resolver.openOutputStream(uri)) {
                if (out == null) throw new IOException("Downloads would not open " + file);
                out.write(bytes);
            }
            values.clear();
            values.put(MediaStore.Downloads.IS_PENDING, 0);
            resolver.update(uri, values, null, null);
        } else {
            // Before Android 10 the shared Downloads needs a permission the shell does not ask for; the
            // app's own Downloads directory is where a file manager finds it instead.
            File dir = context.getExternalFilesDir(Environment.DIRECTORY_DOWNLOADS);
            if (dir == null || (!dir.isDirectory() && !dir.mkdirs())) throw new IOException("No Downloads directory");
            try (OutputStream out = new FileOutputStream(new File(dir, file))) {
                out.write(bytes);
            }
        }
        say("Saved " + file + " to Downloads");
        return file;
    }

    /** Hands a file at an address to the system's download manager, with the page's cookies and user agent. */
    void fetch(String url, String userAgent, String disposition, String type) {
        String file = URLUtil.guessFileName(url, disposition, type);
        DownloadManager.Request request = new DownloadManager.Request(Uri.parse(url))
            .setTitle(file)
            .setMimeType(type)
            .setNotificationVisibility(DownloadManager.Request.VISIBILITY_VISIBLE_NOTIFY_COMPLETED)
            .setDestinationInExternalPublicDir(Environment.DIRECTORY_DOWNLOADS, file);
        String cookies = CookieManager.getInstance().getCookie(url);
        if (cookies != null) request.addRequestHeader("Cookie", cookies);
        if (userAgent != null) request.addRequestHeader("User-Agent", userAgent);
        ((DownloadManager) context.getSystemService(Context.DOWNLOAD_SERVICE)).enqueue(request);
        say("Downloading " + file);
    }

    private void say(final String text) {
        new android.os.Handler(android.os.Looper.getMainLooper()).post(new Runnable() {
            @Override
            public void run() {
                Toast.makeText(context, text, Toast.LENGTH_SHORT).show();
            }
        });
    }

    /**
     * The type a file is filed under: the one its own name says, where the system knows it. Downloads
     * renames a file whose name and type disagree (a log sent as text/plain gains ".txt"), so a name
     * the system knows no type for is filed as plain bytes, which it leaves as named.
     */
    private static String typeOf(String file, String type) {
        String extension = android.webkit.MimeTypeMap.getFileExtensionFromUrl(Uri.encode(file));
        String known = extension.isEmpty() ? null : android.webkit.MimeTypeMap.getSingleton().getMimeTypeFromExtension(extension.toLowerCase(java.util.Locale.ROOT));
        if (known != null) return known;
        if (!extension.isEmpty() || type == null || type.isEmpty()) return "application/octet-stream";
        return type;
    }

    private static String safe(String name) {
        String plain = name == null ? "" : name.replaceAll("[\\\\/:*?\"<>|\\p{Cntrl}]", "_").trim();
        return plain.isEmpty() ? "download" : plain;
    }
}
