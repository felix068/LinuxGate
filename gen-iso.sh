#!/bin/bash
set -e

# Configuration
SYSTEM_LANG="fr_FR.UTF-8"
KEYBOARD_LAYOUT="fr"
KEYBOARD_MODEL="pc105"
TIMEZONE="Europe/Paris"
USERNAME="mint"
PASSWORD="1234"
ISO_FILENAME="mint.iso"

[ "$EUID" -ne 0 ] && { echo "Run with sudo!"; exit 1; }

echo "=== Installing build dependencies ==="
apt update
apt install -y debootstrap squashfs-tools xorriso isolinux syslinux-utils \
    grub-pc-bin grub-efi-amd64-bin mtools dosfstools

WORKDIR="/tmp/debian_live_v9"
rm -rf "$WORKDIR"
mkdir -p "$WORKDIR"/{chroot,iso_build}

echo "=== Creating minimal Debian system ==="
debootstrap --variant=minbase stable "$WORKDIR/chroot" http://deb.debian.org/debian/

echo "=== Mounting filesystems ==="
mount -t proc none "$WORKDIR/chroot/proc"
mount -t sysfs none "$WORKDIR/chroot/sys"
mount --bind /dev "$WORKDIR/chroot/dev"
mount --bind /dev/pts "$WORKDIR/chroot/dev/pts"

echo "=== Installing packages in live system ==="
cat > "$WORKDIR/chroot/setup.sh" << 'SETUPSCRIPT'
#!/bin/bash
set -e
export DEBIAN_FRONTEND=noninteractive
export PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin

apt update
apt install -y linux-image-amd64 live-boot live-boot-initramfs-tools live-config \
    live-config-systemd systemd-sysv initramfs-tools parted fdisk e2fsprogs \
    squashfs-tools dosfstools ntfs-3g sudo nano util-linux coreutils

update-initramfs -u -k all
lsinitramfs /boot/initrd.img-* 2>/dev/null | grep -E "live" | head -5 || true

useradd -m -s /bin/bash -G sudo user 2>/dev/null || true
echo "user:live" | chpasswd
echo "user ALL=(ALL) NOPASSWD:ALL" >> /etc/sudoers

apt clean
rm -rf /var/lib/apt/lists/*
SETUPSCRIPT

chmod +x "$WORKDIR/chroot/setup.sh"
chroot "$WORKDIR/chroot" /setup.sh
rm -f "$WORKDIR/chroot/setup.sh"

mkdir -p "$WORKDIR/chroot/etc/live"
echo "LIVE_MEDIA_PATH=/live" > "$WORKDIR/chroot/etc/live/boot.conf"

# Autologin
mkdir -p "$WORKDIR/chroot/etc/systemd/system/getty@tty1.service.d"
cat > "$WORKDIR/chroot/etc/systemd/system/getty@tty1.service.d/override.conf" << 'EOF'
[Service]
ExecStart=
ExecStart=-/sbin/agetty --autologin root --noclear %I $TERM
Type=idle
EOF

# Auto launch script
cat > "$WORKDIR/chroot/root/.bash_profile" << 'EOF'
#!/bin/bash
FLAG="/tmp/.mint_installer_started"
if [ "$(tty)" = "/dev/tty1" ] && [ ! -f "$FLAG" ]; then
    touch "$FLAG"
    sleep 2
    clear
    [ -x /install-mint.sh ] && /bin/bash /install-mint.sh
fi
exec /bin/bash --login
EOF
chmod +x "$WORKDIR/chroot/root/.bash_profile"

# Installation script
cat > "$WORKDIR/chroot/install-mint.sh" << 'INSTALLSCRIPT'
#!/bin/bash
set -e

[ "$EUID" -ne 0 ] && { echo "Run as root"; exit 1; }

safe_run() { "$@" || echo "WARNING: $* failed"; }

# Read config.txt
CONFIG_FILE=""
for mp in /run/live/medium /lib/live/mount/medium /cdrom; do
    [ -f "$mp/config.txt" ] && { CONFIG_FILE="$mp/config.txt"; break; }
done

SYSTEM_LANG="en_US.UTF-8"
KEYBOARD_LAYOUT="us"
KEYBOARD_MODEL="pc105"
TIMEZONE="UTC"
USERNAME="user"
PASSWORD="password"
COMPUTER_NAME="linux-pc"
ISO_FILENAME="mint.iso"
LINUX_SIZE_GB="30"

if [ -f "$CONFIG_FILE" ]; then
    while IFS='=' read -r key value; do
        [[ -z "$key" || "$key" =~ ^# ]] && continue
        value=$(echo "$value" | sed 's/^"//;s/"$//' | sed "s/^'//;s/'$//")
        case "$key" in
            SYSTEM_LANG) SYSTEM_LANG="$value" ;;
            KEYBOARD_LAYOUT) KEYBOARD_LAYOUT="$value" ;;
            KEYBOARD_MODEL) KEYBOARD_MODEL="$value" ;;
            TIMEZONE) TIMEZONE="$value" ;;
            USERNAME) USERNAME="$value" ;;
            PASSWORD) PASSWORD="$value" ;;
            COMPUTER_NAME) COMPUTER_NAME="$value" ;;
            ISO_FILENAME) ISO_FILENAME="$value" ;;
            LINUX_SIZE_GB) LINUX_SIZE_GB="$value" ;;
        esac
    done < "$CONFIG_FILE"
fi

echo "Config: Lang=$SYSTEM_LANG Keyboard=$KEYBOARD_LAYOUT User=$USERNAME LinuxSize=${LINUX_SIZE_GB}GB"

# Detect disk
TARGET_DISK=""
LIVE_PART=""
for mp in /run/live/medium /lib/live/mount/medium /cdrom; do
    if mountpoint -q "$mp" 2>/dev/null; then
        LIVE_PART=$(findmnt -n -o SOURCE "$mp" 2>/dev/null || true)
        if [ -n "$LIVE_PART" ]; then
            [[ "$LIVE_PART" == *"nvme"* ]] || [[ "$LIVE_PART" == *"mmcblk"* ]] && \
                TARGET_DISK=$(echo "$LIVE_PART" | sed 's/p[0-9]*$//') || \
                TARGET_DISK=$(echo "$LIVE_PART" | sed 's/[0-9]*$//')
            break
        fi
    fi
done

if [ -z "$TARGET_DISK" ]; then
    lsblk -d -o NAME,SIZE,MODEL | grep -E "sd|nvme|mmcblk" || true
    read -rp "Target disk (e.g.: sda): " d
    TARGET_DISK="/dev/$d"
fi

[ ! -b "$TARGET_DISK" ] && { echo "ERROR: $TARGET_DISK not found"; exit 1; }

DISK="$TARGET_DISK"
DISKNAME=$(basename "$DISK")
lsblk -o NAME,SIZE,TYPE,FSTYPE,MOUNTPOINT "$DISK"

PART_TABLE=$(parted "$DISK" print 2>/dev/null | awk -F: '/Partition Table/{gsub(/ /,"",$2);print $2}')
PART_COUNT=$(lsblk -nr -o NAME,TYPE "$DISK" | awk '$2=="part"{c++}END{print c+0}')

# Find Windows partition
WINDOWS_PART=""
WINDOWS_SIZE=0
for pn in 1 2 3 4 5; do
    [[ "$DISKNAME" == nvme* ]] && pdev="${DISK}p${pn}" || pdev="${DISK}${pn}"
    [ -b "$pdev" ] || continue
    pfs=$(blkid -s TYPE -o value "$pdev" 2>/dev/null || echo "")
    [ "$pfs" != "ntfs" ] && continue
    psize=$(($(blockdev --getsize64 "$pdev" 2>/dev/null || echo 0) / 1024 / 1024))
    [ "$psize" -gt 1000 ] && [ "$psize" -gt "$WINDOWS_SIZE" ] && { WINDOWS_PART="$pdev"; WINDOWS_SIZE="$psize"; }
done

[ -z "$WINDOWS_PART" ] && { echo "ERROR: No Windows partition!"; exit 1; }
echo "Windows: $WINDOWS_PART (${WINDOWS_SIZE}MB)"

# Calculate how much more we need to shrink Windows
# LINUX_SIZE_GB includes the 2GB FAT32, so we need (LINUX_SIZE_GB - 2) more for ext4
LINUX_SIZE_MB=$((LINUX_SIZE_GB * 1024))
FAT32_SIZE_MB=2048

# Get current free space on disk
CURRENT_FREE_MB=0
while IFS= read -r line; do
    if echo "$line" | grep -qi "Free Space"; then
        vals=($(echo "$line" | grep -oE '[0-9]+(\.[0-9]+)?MB' | sed 's/MB//'))
        [ "${#vals[@]}" -ge 3 ] && {
            sz=${vals[2]%%.*}
            [ "$sz" -gt "$CURRENT_FREE_MB" ] 2>/dev/null && CURRENT_FREE_MB=$sz
        }
    fi
done <<< "$(parted "$DISK" unit MB print free 2>/dev/null)"

echo "Current free space: ${CURRENT_FREE_MB}MB"
echo "Desired Linux size: ${LINUX_SIZE_MB}MB (${LINUX_SIZE_GB}GB)"

# Calculate how much more shrinking is needed
# We want: LINUX_SIZE_MB total for Linux (including FAT32)
# Current free space is what Windows already gave us
# Additional shrink needed = LINUX_SIZE_MB - CURRENT_FREE_MB
ADDITIONAL_SHRINK_MB=$((LINUX_SIZE_MB - CURRENT_FREE_MB))

if [ "$ADDITIONAL_SHRINK_MB" -gt 1024 ]; then
    echo "=== Additional NTFS shrinking needed: ${ADDITIONAL_SHRINK_MB}MB ==="

    # Calculate new Windows size
    NEW_WINDOWS_SIZE_MB=$((WINDOWS_SIZE - ADDITIONAL_SHRINK_MB))

    if [ "$NEW_WINDOWS_SIZE_MB" -lt 20480 ]; then
        echo "WARNING: New Windows size would be less than 20GB, limiting shrink"
        NEW_WINDOWS_SIZE_MB=20480
        ADDITIONAL_SHRINK_MB=$((WINDOWS_SIZE - NEW_WINDOWS_SIZE_MB))
    fi

    echo "Shrinking Windows from ${WINDOWS_SIZE}MB to ${NEW_WINDOWS_SIZE_MB}MB..."

    # Make sure partition is not mounted
    umount "$WINDOWS_PART" 2>/dev/null || true

    # Check filesystem first
    echo "Checking NTFS filesystem..."
    ntfsfix "$WINDOWS_PART" || true

    # Resize NTFS filesystem (size in bytes for ntfsresize)
    NEW_SIZE_BYTES=$((NEW_WINDOWS_SIZE_MB * 1024 * 1024))
    echo "Resizing NTFS to ${NEW_WINDOWS_SIZE_MB}MB..."
    ntfsresize -f -s "${NEW_SIZE_BYTES}" "$WINDOWS_PART" <<< "y" || {
        echo "WARNING: ntfsresize failed, continuing with available space"
    }

    # Resize partition table
    PART_NUM=$(echo "$WINDOWS_PART" | grep -oE '[0-9]+$')
    echo "Resizing partition table..."
    parted -s "$DISK" resizepart "$PART_NUM" "${NEW_WINDOWS_SIZE_MB}MB" 2>/dev/null || true

    sync
    partprobe "$DISK" 2>/dev/null || true
    sleep 2

    # Update Windows size
    WINDOWS_SIZE=$(($(blockdev --getsize64 "$WINDOWS_PART" 2>/dev/null || echo 0) / 1024 / 1024))
    echo "Windows partition now: ${WINDOWS_SIZE}MB"
else
    echo "No additional shrinking needed (current free space is sufficient)"
fi

mkdir -p /mnt/windows
mount -t ntfs-3g "$WINDOWS_PART" /mnt/windows

[ ! -f "/mnt/windows/$ISO_FILENAME" ] && { echo "ERROR: $ISO_FILENAME not found!"; ls /mnt/windows/ | head -15; umount /mnt/windows; exit 1; }
echo "ISO found: $(du -h /mnt/windows/$ISO_FILENAME | cut -f1)"

# Recovery backup (MBR 4 partitions)
BACKUP_DONE=0
if [ "$PART_TABLE" = "msdos" ] && [ "$PART_COUNT" -ge 4 ]; then
    RECOVERY_PART=""
    RECOVERY_NUM=""
    for pn in 4 3 2 1; do
        [[ "$DISKNAME" == nvme* ]] && pdev="${DISK}p${pn}" || pdev="${DISK}${pn}"
        [ -b "$pdev" ] || continue
        psize_bytes=$(blockdev --getsize64 "$pdev" 2>/dev/null || echo 0)
        psize_mb=$((psize_bytes / 1024 / 1024))
        pfs=$(blkid -s TYPE -o value "$pdev" 2>/dev/null || echo "")
        [ "$psize_mb" -ge 200 ] && [ "$psize_mb" -le 800 ] && [ "$pfs" = "ntfs" ] && [ "$pdev" != "$LIVE_PART" ] && \
            { RECOVERY_PART="$pdev"; RECOVERY_NUM="$pn"; break; }
    done

    if [ -n "$RECOVERY_PART" ]; then
        echo "Backing up recovery: $RECOVERY_PART"
        umount "$RECOVERY_PART" 2>/dev/null || true
        dd if="$RECOVERY_PART" of=/mnt/windows/recovery_backup.img bs=4M status=progress conv=fsync
        sync
        IMG_SIZE=$(stat -c%s /mnt/windows/recovery_backup.img 2>/dev/null || echo 0)
        PART_SIZE=$(blockdev --getsize64 "$RECOVERY_PART" 2>/dev/null || echo 0)
        if [ "$IMG_SIZE" -eq "$PART_SIZE" ] && [ "$IMG_SIZE" -gt 0 ]; then
            echo "$PART_SIZE" > /mnt/windows/recovery_size.txt
            parted -s "$DISK" rm "$RECOVERY_NUM"
            sync; sleep 2; partprobe "$DISK" 2>/dev/null || true
            BACKUP_DONE=1
            PART_COUNT=$((PART_COUNT - 1))
        else
            rm -f /mnt/windows/recovery_backup.img
        fi
    fi
fi

# Find free space and create partition
partprobe "$DISK" 2>/dev/null || true; sleep 1
max_size=0; best_start=""; best_end=""
while IFS= read -r line; do
    if echo "$line" | grep -qi "Free Space"; then
        vals=($(echo "$line" | grep -oE '[0-9]+(\.[0-9]+)?MB' | sed 's/MB//'))
        [ "${#vals[@]}" -ge 3 ] || continue
        sz=${vals[2]%%.*}
        [ "$sz" -gt "$max_size" ] 2>/dev/null && { max_size=$sz; best_start=${vals[0]%%.*}; best_end=${vals[1]%%.*}; }
    fi
done <<< "$(parted "$DISK" unit MB print free 2>/dev/null)"

[ "$max_size" -lt 5000 ] && { echo "ERROR: <5GB free"; umount /mnt/windows; exit 1; }

echo "Creating Linux partition (${max_size}MB)"
parted -s "$DISK" mkpart primary ext4 "${best_start}MB" "${best_end}MB"
sync; partprobe "$DISK" 2>/dev/null || true; sleep 2

NEW_PART=""
for i in 1 2 3 4 5; do
    [[ "$DISKNAME" == nvme* ]] && tp="${DISK}p${i}" || tp="${DISK}${i}"
    [ -b "$tp" ] || continue
    fs=$(blkid -s TYPE -o value "$tp" 2>/dev/null || echo "")
    [ -z "$fs" ] && { NEW_PART="$tp"; break; }
done
[ -z "$NEW_PART" ] && NEW_PART=$(lsblk -nr -o NAME,TYPE "$DISK" | awk '$2=="part"{p="/dev/"$1}END{print p}')
NEW_PART_NUM=$(echo "$NEW_PART" | grep -oE '[0-9]+$')

mkfs.ext4 -F "$NEW_PART"
mkdir -p /mnt/target /mnt/iso
mount "$NEW_PART" /mnt/target

# Extract ISO
echo "Copying ISO..."
cp "/mnt/windows/$ISO_FILENAME" /mnt/target/mint.iso
rm -f "/mnt/windows/$ISO_FILENAME"

mount -o loop /mnt/target/mint.iso /mnt/iso
echo "Extracting system..."
unsquashfs -f -d /mnt/target /mnt/iso/casper/filesystem.squashfs
umount /mnt/iso
rm -f /mnt/target/mint.iso

# System config
mount -t proc none /mnt/target/proc
mount -t sysfs none /mnt/target/sys
mount --bind /dev /mnt/target/dev
mount --bind /dev/pts /mnt/target/dev/pts

UUID=$(blkid -s UUID -o value "$NEW_PART")
echo "UUID=$UUID / ext4 defaults 0 1" > /mnt/target/etc/fstab

for pn in 1 2 3 4; do
    [[ "$DISKNAME" == nvme* ]] && pdev="${DISK}p${pn}" || pdev="${DISK}${pn}"
    [ -b "$pdev" ] || continue
    [ "$pdev" = "$NEW_PART" ] && continue
    pfs=$(blkid -s TYPE -o value "$pdev" 2>/dev/null || echo "")
    if [ "$pfs" = "ntfs" ]; then
        mdir="/mnt/target/mnt/win_$pn"
        mkdir -p "$mdir"
        mount -t ntfs-3g -o ro "$pdev" "$mdir" 2>/dev/null || true
    fi
done

RECOVERY_IMG=""
RECOVERY_SIZE_BYTES=""
if [ "$BACKUP_DONE" = "1" ]; then
    RECOVERY_IMG="/mnt/windows/recovery_backup.img"
    RECOVERY_SIZE_BYTES=$(cat /mnt/windows/recovery_size.txt)
fi

# Chroot config
chroot /mnt/target /bin/bash << CHROOTSCRIPT
set -e

useradd -m -s /bin/bash -G sudo,adm,cdrom,audio,video,plugdev "$USERNAME" 2>/dev/null || true
echo "$USERNAME:$PASSWORD" | chpasswd
echo "$USERNAME ALL=(ALL) NOPASSWD:ALL" >> /etc/sudoers
echo "$COMPUTER_NAME" > /etc/hostname

# Windows mount
WIN_UUID=""
for pn in 1 2 3 4; do
    [[ "$DISKNAME" == nvme* ]] && pdev="${DISK}p${pn}" || pdev="${DISK}${pn}"
    [ -b "$pdev" ] || continue
    pfs=$(blkid -s TYPE -o value "$pdev" 2>/dev/null || echo "")
    if [ "$pfs" = "ntfs" ]; then
        psize=$(($(blockdev --getsize64 "$pdev" 2>/dev/null || echo 0) / 1024 / 1024 / 1024))
        [ "$psize" -gt 10 ] && { WIN_UUID=$(blkid -s UUID -o value "$pdev"); break; }
    fi
done
[ -n "$WIN_UUID" ] && {
    mkdir -p /mnt/windows
    echo "UUID=$WIN_UUID /mnt/windows ntfs-3g defaults,uid=1000,gid=1000,dmask=022,fmask=133,windows_names,nofail 0 0" >> /etc/fstab
}

# Locale
sed -i "s/# $SYSTEM_LANG/$SYSTEM_LANG/" /etc/locale.gen 2>/dev/null || true
locale-gen 2>/dev/null || true
cat > /etc/default/locale << EOF
LANG=$SYSTEM_LANG
LC_ALL=$SYSTEM_LANG
LANGUAGE=${SYSTEM_LANG%%_*}
EOF

# Keyboard
cat > /etc/default/keyboard << EOF
XKBMODEL="$KEYBOARD_MODEL"
XKBLAYOUT="$KEYBOARD_LAYOUT"
XKBVARIANT=""
XKBOPTIONS=""
BACKSPACE="guess"
EOF

mkdir -p /etc/X11/xorg.conf.d
cat > /etc/X11/xorg.conf.d/00-keyboard.conf << EOF
Section "InputClass"
    Identifier "system-keyboard"
    MatchIsKeyboard "on"
    Option "XkbLayout" "$KEYBOARD_LAYOUT"
    Option "XkbModel" "$KEYBOARD_MODEL"
EndSection
EOF

mkdir -p /etc/dconf/db/local.d
cat > /etc/dconf/db/local.d/00-keyboard << EOF
[org/gnome/libgnomekbd/keyboard]
layouts=['$KEYBOARD_LAYOUT']
model='$KEYBOARD_MODEL'
[org/cinnamon/desktop/input-sources]
sources=[('xkb', '$KEYBOARD_LAYOUT')]
EOF
dconf update 2>/dev/null || true

# Timezone
ln -sf /usr/share/zoneinfo/$TIMEZONE /etc/localtime
echo "$TIMEZONE" > /etc/timezone

# GRUB
cat > /etc/default/grub << 'GRUBCFG'
GRUB_DEFAULT=0
GRUB_TIMEOUT=10
GRUB_TIMEOUT_STYLE=menu
GRUB_DISTRIBUTOR="Linux Mint"
GRUB_CMDLINE_LINUX_DEFAULT="quiet splash"
GRUB_CMDLINE_LINUX=""
GRUB_DISABLE_OS_PROBER=true
GRUB_RECORDFAIL_TIMEOUT=10
GRUBCFG

rm -f /etc/default/grub.d/50_linuxmint.cfg 2>/dev/null || true

WIN_BOOT_UUID=""
for pn in 1 2 3 4; do
    [[ "$DISKNAME" == nvme* ]] && pdev="${DISK}p${pn}" || pdev="${DISK}${pn}"
    [ -b "$pdev" ] || continue
    pfs=$(blkid -s TYPE -o value "$pdev" 2>/dev/null || echo "")
    if [ "$pfs" = "ntfs" ]; then
        tmpmnt=$(mktemp -d)
        mount -t ntfs-3g -o ro "$pdev" "$tmpmnt" 2>/dev/null || continue
        if [ -f "$tmpmnt/bootmgr" ]; then
            WIN_BOOT_UUID=$(blkid -s UUID -o value "$pdev")
            umount "$tmpmnt"; rmdir "$tmpmnt"
            break
        fi
        umount "$tmpmnt" 2>/dev/null || true
        rmdir "$tmpmnt" 2>/dev/null || true
    fi
done

if [ -n "$WIN_BOOT_UUID" ]; then
    cat > /etc/grub.d/40_custom << GRUBCUSTOM
#!/bin/sh
exec tail -n +3 \$0
menuentry "Windows 10" --class windows --class os {
    insmod part_msdos
    insmod ntfs
    insmod ntldr
    search --no-floppy --fs-uuid --set=root $WIN_BOOT_UUID
    ntldr /bootmgr
}
GRUBCUSTOM
    chmod +x /etc/grub.d/40_custom
else
    sed -i 's/GRUB_DISABLE_OS_PROBER=true/GRUB_DISABLE_OS_PROBER=false/' /etc/default/grub
fi

grub-install --target=i386-pc --recheck "$DISK" || true
os-prober 2>/dev/null || true
update-grub 2>/dev/null || true

# First boot resize
cat > /usr/local/bin/first-boot-resize.sh << 'FIRSTBOOT'
#!/bin/bash
LOG="/tmp/first-boot-resize.log"
echo "First boot resize - \$(date)" > "\$LOG"
ROOT_DEV=\$(findmnt -n -o SOURCE /)
resize2fs "\$ROOT_DEV" >> "\$LOG" 2>&1
systemctl disable first-boot-resize.service
rm -f /etc/systemd/system/first-boot-resize.service /usr/local/bin/first-boot-resize.sh
FIRSTBOOT
chmod +x /usr/local/bin/first-boot-resize.sh

cat > /etc/systemd/system/first-boot-resize.service << 'SERVICEUNIT'
[Unit]
Description=Resize root filesystem on first boot
After=local-fs.target
[Service]
Type=oneshot
ExecStart=/usr/local/bin/first-boot-resize.sh
RemainAfterExit=no
[Install]
WantedBy=multi-user.target
SERVICEUNIT
systemctl enable first-boot-resize.service
CHROOTSCRIPT

# Cleanup mounts
for pn in 1 2 3 4; do
    mdir="/mnt/target/mnt/win_$pn"
    [ -d "$mdir" ] && umount "$mdir" 2>/dev/null || true
done

parted -s "$DISK" set "$NEW_PART_NUM" boot on 2>/dev/null || true

umount /mnt/target/dev/pts 2>/dev/null || true
umount /mnt/target/dev 2>/dev/null || true
umount /mnt/target/proc 2>/dev/null || true
umount /mnt/target/sys 2>/dev/null || true
umount /mnt/target 2>/dev/null || true

# Recovery restoration
if [ "$BACKUP_DONE" = "1" ] && [ -f "$RECOVERY_IMG" ]; then
    RECOVERY_SIZE_MB=$((RECOVERY_SIZE_BYTES / 1024 / 1024))

    if [ -n "$LIVE_PART" ]; then
        LIVE_PART_NUM=$(echo "$LIVE_PART" | grep -oE '[0-9]+$')
        for mp in /run/live/medium /lib/live/mount/medium /cdrom; do
            umount -l "$mp" 2>/dev/null || true
        done
        parted -s "$DISK" rm "$LIVE_PART_NUM" 2>/dev/null || true
        sync; sleep 2; partprobe "$DISK" 2>/dev/null || true
    fi

    DISK_SIZE_MB=$(($(blockdev --getsize64 "$DISK") / 1024 / 1024))
    END_MB=$((DISK_SIZE_MB - 1))
    START_MB=$((END_MB - RECOVERY_SIZE_MB))

    parted -s "$DISK" mkpart primary ntfs ${START_MB}MiB ${END_MB}MiB 2>&1 || true
    sync; sleep 2

    SECTOR_SIZE=$(blockdev --getss "$DISK")
    START_SECTOR=$((START_MB * 1024 * 1024 / SECTOR_SIZE))
    dd if="$RECOVERY_IMG" of="$DISK" bs=512 seek="$START_SECTOR" status=progress conv=notrunc
    sync
    partprobe "$DISK" 2>/dev/null || true
    sleep 1

    # Find and set msftres flag on restored recovery partition
    RECOVERY_PART_NUM=""
    for pn in 1 2 3 4 5; do
        [[ "$DISKNAME" == nvme* ]] && pdev="${DISK}p${pn}" || pdev="${DISK}${pn}"
        [ -b "$pdev" ] || continue
        pstart=$(parted -s "$DISK" unit MiB print 2>/dev/null | awk -v n="$pn" '$1==n {gsub(/MiB/,"",$2); print int($2)}')
        [ "$pstart" -ge "$((START_MB - 10))" ] && [ "$pstart" -le "$((START_MB + 10))" ] && { RECOVERY_PART_NUM="$pn"; break; }
    done
    [ -n "$RECOVERY_PART_NUM" ] && {
        echo "Setting msftres flag on partition $RECOVERY_PART_NUM"
        parted -s "$DISK" set "$RECOVERY_PART_NUM" msftres on 2>/dev/null || true
    }

    for pn in 1 2 3 4 5; do
        [[ "$DISKNAME" == nvme* ]] && pdev="${DISK}p${pn}" || pdev="${DISK}${pn}"
        [ -b "$pdev" ] || continue
        pfs=$(blkid -s TYPE -o value "$pdev" 2>/dev/null || echo "")
        if [ "$pfs" = "ext4" ]; then
            NEW_END=$((START_MB - 1))
            parted -s "$DISK" resizepart "$pn" "${NEW_END}MiB" 2>/dev/null || true
            sync
            e2fsck -f -y "$pdev" 2>/dev/null || true
            resize2fs "$pdev" 2>/dev/null || true
            break
        fi
    done

    rm -f "$RECOVERY_IMG" /mnt/windows/recovery_size.txt
fi

umount /mnt/windows 2>/dev/null || true
parted -s "$DISK" set "$NEW_PART_NUM" boot on 2>/dev/null || true

echo ""
echo "=== INSTALLATION COMPLETED ==="
echo "Rebooting in 1s..."
sleep 1
reboot
INSTALLSCRIPT

chmod +x "$WORKDIR/chroot/install-mint.sh"

echo "=== Unmounting chroot ==="
umount "$WORKDIR/chroot/dev/pts" 2>/dev/null || true
umount "$WORKDIR/chroot/dev" 2>/dev/null || true
umount "$WORKDIR/chroot/proc" 2>/dev/null || true
umount "$WORKDIR/chroot/sys" 2>/dev/null || true

# Create config.txt
cat > "$WORKDIR/iso_build/config.txt" << CONFIGFILE
SYSTEM_LANG="$SYSTEM_LANG"
KEYBOARD_LAYOUT="$KEYBOARD_LAYOUT"
KEYBOARD_MODEL="$KEYBOARD_MODEL"
TIMEZONE="$TIMEZONE"
USERNAME="$USERNAME"
PASSWORD="$PASSWORD"
COMPUTER_NAME="mint-pc"
ISO_FILENAME="$ISO_FILENAME"
LINUX_SIZE_GB="30"
CONFIGFILE

echo "=== Creating squashfs ==="
mkdir -p "$WORKDIR/iso_build/live"
mksquashfs "$WORKDIR/chroot" "$WORKDIR/iso_build/live/filesystem.squashfs" -comp xz -b 1M -e boot

cp "$WORKDIR/chroot/boot/vmlinuz-"* "$WORKDIR/iso_build/live/vmlinuz"
cp "$WORKDIR/chroot/boot/initrd.img-"* "$WORKDIR/iso_build/live/initrd.img"

echo "=== Configuring ISOLINUX ==="
mkdir -p "$WORKDIR/iso_build/isolinux"
cp /usr/lib/ISOLINUX/isolinux.bin "$WORKDIR/iso_build/isolinux/"
cp /usr/lib/syslinux/modules/bios/*.c32 "$WORKDIR/iso_build/isolinux/"

cat > "$WORKDIR/iso_build/isolinux/isolinux.cfg" << 'EOF'
UI menu.c32
PROMPT 0
TIMEOUT 30
DEFAULT live
MENU TITLE Libertix Installer
LABEL live
    MENU LABEL Install Linux Mint (Automatic)
    KERNEL /live/vmlinuz
    APPEND initrd=/live/initrd.img boot=live toram components quiet splash
LABEL live-verbose
    MENU LABEL Install (Verbose mode)
    KERNEL /live/vmlinuz
    APPEND initrd=/live/initrd.img boot=live toram components
EOF

echo "=== Configuring GRUB EFI ==="
mkdir -p "$WORKDIR/iso_build/boot/grub" "$WORKDIR/iso_build/EFI/BOOT"

cat > "$WORKDIR/iso_build/boot/grub/grub.cfg" << 'EOF'
set timeout=5
set default=0
menuentry "Install Linux Mint (Automatic)" {
    linux /live/vmlinuz boot=live toram components quiet splash
    initrd /live/initrd.img
}
menuentry "Install (Verbose mode)" {
    linux /live/vmlinuz boot=live toram components
    initrd /live/initrd.img
}
EOF

grub-mkstandalone --format=x86_64-efi \
    --output="$WORKDIR/iso_build/EFI/BOOT/bootx64.efi" \
    --locales="" --fonts="" \
    "boot/grub/grub.cfg=$WORKDIR/iso_build/boot/grub/grub.cfg"

dd if=/dev/zero of="$WORKDIR/iso_build/boot/grub/efi.img" bs=1M count=10
mkfs.vfat "$WORKDIR/iso_build/boot/grub/efi.img"
mmd -i "$WORKDIR/iso_build/boot/grub/efi.img" ::/EFI ::/EFI/BOOT
mcopy -i "$WORKDIR/iso_build/boot/grub/efi.img" \
    "$WORKDIR/iso_build/EFI/BOOT/bootx64.efi" ::/EFI/BOOT/

echo "=== Creating ISO ==="
xorriso -as mkisofs \
    -r -J -joliet-long \
    -V "LIBERTIX_INSTALLER" \
    -o ./libertix-installer.iso \
    -isohybrid-mbr /usr/lib/ISOLINUX/isohdpfx.bin \
    -c isolinux/boot.cat \
    -b isolinux/isolinux.bin \
    -no-emul-boot -boot-load-size 4 -boot-info-table \
    -eltorito-alt-boot \
    -e boot/grub/efi.img \
    -no-emul-boot -isohybrid-gpt-basdat \
    "$WORKDIR/iso_build"

rm -rf "$WORKDIR"

echo "=== Done: libertix-installer.iso ($(du -h ./libertix-installer.iso | cut -f1)) ==="
