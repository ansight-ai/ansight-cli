package ai.ansight.host;

import android.content.Context;
import android.graphics.Bitmap;
import android.graphics.Canvas;
import android.graphics.drawable.Drawable;
import android.os.Looper;
import android.os.UserHandle;
import android.util.Base64;
import java.io.ByteArrayOutputStream;

// Executed by ADB's app_process as the shell user; never installed in the app.
public final class AndroidAppIcon {
    public static void main(String[] args) throws Exception {
        if (args.length != 2) throw new IllegalArgumentException("package and user are required");
        Looper.prepareMainLooper();
        Class<?> activityThread = Class.forName("android.app.ActivityThread");
        Object thread = activityThread.getMethod("systemMain").invoke(null);
        Context context = (Context) activityThread.getMethod("getSystemContext").invoke(thread);
        UserHandle user = (UserHandle) UserHandle.class.getMethod("of", int.class)
            .invoke(null, Integer.parseInt(args[1]));
        context = (Context) Context.class.getMethod("createContextAsUser", UserHandle.class, int.class)
            .invoke(context, user, 0);
        Drawable icon = context.getPackageManager().getApplicationIcon(args[0]);
        Bitmap bitmap = Bitmap.createBitmap(192, 192, Bitmap.Config.ARGB_8888);
        icon.setBounds(0, 0, bitmap.getWidth(), bitmap.getHeight());
        icon.draw(new Canvas(bitmap));
        ByteArrayOutputStream png = new ByteArrayOutputStream();
        bitmap.compress(Bitmap.CompressFormat.PNG, 100, png);
        bitmap.recycle();
        System.out.println("ansight-icon:" + Base64.encodeToString(png.toByteArray(), Base64.NO_WRAP));
    }
}
