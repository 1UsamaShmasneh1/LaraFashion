(function () {
    const MAX_IMAGE_SIDE = 1200;
    const MAX_UPLOAD_SIZE = 10 * 1024 * 1024;

    function setUploadStatus(message, color) {
        const statusElement = document.getElementById("productImageUploadStatus");
        if (!statusElement) return;
        statusElement.textContent = message || "";
        statusElement.style.color = color || "#0f766e";
    }

    function getAdminToken() {
        return localStorage.getItem("admin_token") || "";
    }

    function getFileBaseName(fileName) {
        const originalName = fileName || "product-image";
        return originalName.replace(/\.[^/.]+$/, "") || "product-image";
    }

    async function convertImageFileToPng(file) {
        if (!file || !file.type || !file.type.startsWith("image/")) {
            throw new Error("Invalid image file.");
        }

        const objectUrl = URL.createObjectURL(file);

        try {
            const image = await new Promise((resolve, reject) => {
                const img = new Image();
                img.onload = () => resolve(img);
                img.onerror = () => reject(new Error("Could not read image in browser."));
                img.src = objectUrl;
            });

            let width = image.naturalWidth || image.width;
            let height = image.naturalHeight || image.height;

            if (!width || !height) {
                throw new Error("Invalid image dimensions.");
            }

            const ratio = Math.min(1, MAX_IMAGE_SIDE / Math.max(width, height));
            width = Math.max(1, Math.round(width * ratio));
            height = Math.max(1, Math.round(height * ratio));

            const canvas = document.createElement("canvas");
            canvas.width = width;
            canvas.height = height;

            const context = canvas.getContext("2d", { alpha: false });
            if (!context) {
                throw new Error("Canvas is not supported.");
            }

            context.fillStyle = "#ffffff";
            context.fillRect(0, 0, width, height);
            context.drawImage(image, 0, 0, width, height);

            const blob = await new Promise((resolve, reject) => {
                canvas.toBlob((result) => {
                    if (result) resolve(result);
                    else reject(new Error("Could not convert image to PNG in browser."));
                }, "image/png", 0.92);
            });

            return new File([blob], `${getFileBaseName(file.name)}.png`, { type: "image/png" });
        } finally {
            URL.revokeObjectURL(objectUrl);
        }
    }

    async function uploadFile(file, token) {
        const formData = new FormData();
        formData.append("file", file, file.name || "product-image");

        const response = await fetch("/api/admin/upload-product-image", {
            method: "POST",
            headers: {
                "X-Admin-Token": token
            },
            body: formData
        });

        if (!response.ok) {
            let message = "فشل رفع الصورة.";
            try {
                const contentType = response.headers.get("content-type") || "";
                if (contentType.includes("application/json")) {
                    const error = await response.json();
                    if (error && error.message) message = error.message;
                } else {
                    const text = await response.text();
                    if (text) message = text;
                }
            } catch { }
            throw new Error(message);
        }

        return await response.json();
    }

    function setUploadedImageUrl(imageUrl) {
        const hiddenInput = document.getElementById("productImageUrlInput");
        if (!hiddenInput) return;

        hiddenInput.value = imageUrl || "";
        hiddenInput.dispatchEvent(new Event("change", { bubbles: true }));
        hiddenInput.dispatchEvent(new Event("input", { bubbles: true }));
    }

    window.laraFashionUploadProductImage = async function (input) {
        try {
            if (!input || !input.files || input.files.length === 0) {
                return;
            }

            const originalFile = input.files[0];
            const token = getAdminToken();

            if (!token) {
                setUploadStatus("يجب تسجيل الدخول قبل رفع الصورة.", "#dc2626");
                return;
            }

            setUploadStatus("جاري تجهيز الصورة...", "#0f766e");

            let fileToUpload = originalFile;
            let convertedOnClient = false;

            try {
                const pngFile = await convertImageFileToPng(originalFile);

                if (pngFile && pngFile.size > 0 && pngFile.size <= MAX_UPLOAD_SIZE) {
                    fileToUpload = pngFile;
                    convertedOnClient = true;
                }
            } catch (conversionError) {
                console.warn("Client PNG conversion failed. Uploading original file to server fallback.", conversionError);
            }

            if (fileToUpload.size > MAX_UPLOAD_SIZE) {
                setUploadStatus("الصورة كبيرة جداً. اختر صورة أصغر.", "#dc2626");
                return;
            }

            setUploadStatus(
                convertedOnClient
                    ? "تم تحويل الصورة إلى PNG. جاري الرفع..."
                    : "تعذر التحويل داخل الهاتف. جاري رفع الصورة ليتم تحويلها في السيرفر...",
                "#0f766e");

            const result = await uploadFile(fileToUpload, token);
            const imageUrl = result.imageUrl || result.ImageUrl || "";

            if (!imageUrl) {
                setUploadStatus("تم الرفع لكن لم يرجع رابط الصورة.", "#dc2626");
                return;
            }

            setUploadedImageUrl(imageUrl);
            input.setAttribute("data-uploaded-url", imageUrl);

            setUploadStatus("تم رفع الصورة وتحويلها إلى PNG. اضغط حفظ المنتج.", "#16a34a");
        } catch (error) {
            console.error("Product image upload failed", error);
            setUploadStatus(error && error.message ? error.message : "فشل رفع الصورة.", "#dc2626");
        }
    };

    window.laraFashionGetUploadedProductImageUrl = function () {
        const hiddenInput = document.getElementById("productImageUrlInput");
        return hiddenInput ? hiddenInput.value : "";
    };
})();
