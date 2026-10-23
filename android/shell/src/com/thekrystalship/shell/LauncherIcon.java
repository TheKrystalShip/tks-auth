package com.thekrystalship.shell;

import android.content.ComponentName;
import android.content.Context;
import android.content.SharedPreferences;
import android.content.pm.PackageManager;

/**
 * The launcher's icon in the palette the page shows. An app whose {@code res/} carries an icon per
 * palette ({@code mipmap/icon_<palette>}, with {@code -} written {@code _}) is built with a launcher
 * entry per icon, each an alias of {@link ShellActivity} named {@link #PREFIX}{@code <palette>}, and
 * only the one {@code R.string.icon_default} names enabled. The page says which palette it shows
 * ({@link Bridge#icon}); the entry is changed when the app leaves the screen, since a launcher redraws
 * an app whose entry changes and some close what was opened from the old one.
 *
 * An app with no icon per palette has no such entries, and nothing here changes anything.
 */
final class LauncherIcon {
    /** The name of the launcher entry for a palette, before the palette. */
    static final String PREFIX = "com.thekrystalship.shell.icon.";
    private static final String SHOWN = "icon";

    private final Context context;
    private final SharedPreferences prefs;
    private volatile String wanted;

    LauncherIcon(Context context) {
        this.context = context;
        this.prefs = context.getSharedPreferences("shell", Context.MODE_PRIVATE);
    }

    /** The palette the page shows; one with no entry of its own leaves the icon as it is. */
    void want(String palette) {
        if (palette == null || !palette.matches("[a-z0-9][a-z0-9-]*")) return;
        String entry = palette.replace('-', '_');
        if (exists(entry)) wanted = entry;
    }

    /** Enables the wanted palette's entry and disables the one shown, if they differ. */
    void apply() {
        String next = wanted;
        String shown = prefs.getString(SHOWN, context.getString(R.string.icon_default));
        if (next == null || next.equals(shown) || shown.isEmpty()) return;
        PackageManager pm = context.getPackageManager();
        pm.setComponentEnabledSetting(component(next), PackageManager.COMPONENT_ENABLED_STATE_ENABLED,
            PackageManager.DONT_KILL_APP);
        pm.setComponentEnabledSetting(component(shown), PackageManager.COMPONENT_ENABLED_STATE_DISABLED,
            PackageManager.DONT_KILL_APP);
        prefs.edit().putString(SHOWN, next).apply();
    }

    private ComponentName component(String entry) {
        return new ComponentName(context.getPackageName(), PREFIX + entry);
    }

    private boolean exists(String entry) {
        try {
            context.getPackageManager().getActivityInfo(component(entry), PackageManager.MATCH_DISABLED_COMPONENTS);
            return true;
        } catch (PackageManager.NameNotFoundException absent) {
            return false;
        }
    }
}
