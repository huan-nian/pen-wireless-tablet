import java.util.Properties

plugins {
    alias(libs.plugins.android.application)
}

/**
 * 正式签名的密钥库配置。
 *
 * 密钥库本身与密码**不进仓库**（见仓库根 signing/.gitignore）：
 *   - 本机：放在仓库根的 signing/（keystore 文件 + keystore.properties）；
 *   - CI：由 GitHub Secrets 注入（见 .github/workflows/release.yml）。
 *
 * 为什么这么放：密钥库是发布者身份，泄露它等于任何人都能以你的名义发版；
 * 而且它一旦丢失就无法再给已发布的版本做升级。
 *
 * 文件不存在时 release 构建会退化为**未签名**（不是用 debug 签名冒充），
 * 这样至少不会误发一个「看起来是正式版、其实是 debug 签名」的包。
 */
// rootProject 是 PenClient，而 signing/ 在仓库根，所以要再上一级
val keystorePropertiesFile = rootProject.file("../signing/keystore.properties")
val keystoreProperties = Properties().apply {
    if (keystorePropertiesFile.exists()) {
        keystorePropertiesFile.inputStream().use { load(it) }
    }
}

android {
    namespace = "com.HuanNian.penclient"
    compileSdk {
        version = release(36) {
            minorApiLevel = 1
        }
    }

    defaultConfig {
        applicationId = "com.HuanNian.penclient"
        minSdk = 26
        targetSdk = 36

        // versionCode 是给系统比较新旧用的整数，每次发版必须递增；
        // versionName 是给人看的，两者要一起改。
        // 1.0.1：新增触摸模式（手指也能书写）
        versionCode = 2
        versionName = "1.0.1"

        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
    }

    signingConfigs {
        if (keystorePropertiesFile.exists()) {
            create("release") {
                storeFile = file(keystoreProperties.getProperty("storeFile"))
                storePassword = keystoreProperties.getProperty("storePassword")
                keyAlias = keystoreProperties.getProperty("keyAlias")
                keyPassword = keystoreProperties.getProperty("keyPassword")

                // v1/v2 都开：v2 是新设备的校验方式，v1 兼容旧系统
                enableV1Signing = true
                enableV2Signing = true
            }
        }
    }

    buildTypes {
        debug {
            // 给调试版加包名后缀，这样它能和正式版**同时装在一台设备上**，
            // 便于一边用正式版一边对比调试版；也避免调试版覆盖掉正式版。
            applicationIdSuffix = ".debug"
            versionNameSuffix = "-debug"
        }

        release {
            optimization {
                enable = false
            }

            signingConfig = if (keystorePropertiesFile.exists()) {
                signingConfigs.getByName("release")
            } else {
                logger.warn(
                    "未找到 signing/keystore.properties，release 包将不会被签名。" +
                        "如需正式签名版，请先按 README「正式签名」一节生成密钥库。"
                )
                null
            }
        }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_11
        targetCompatibility = JavaVersion.VERSION_11
    }
    buildFeatures {
        viewBinding = true
    }
}

dependencies {
    implementation(libs.androidx.activity.ktx)
    implementation(libs.androidx.appcompat)
    implementation(libs.androidx.constraintlayout)
    implementation(libs.androidx.core.ktx)
    implementation(libs.material)
    testImplementation(libs.junit)
    androidTestImplementation(libs.androidx.espresso.core)
    androidTestImplementation(libs.androidx.junit)
}
