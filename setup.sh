echo hello1 > log.txt

zippassword=$1
bloburl=$2
storagekey=$3

sudo apt update
sudo apt upgrade -y
sudo apt autoremove -y

wget -O azcopy.tar.gz https://aka.ms/downloadazcopylinux64
tar -xf azcopy.tar.gz
sudo ./install.sh

sudo apt install p7zip-full -y

mkdir payload
7z x payload.7z -opayload -p$zippassword

sudo apt install npm nodejs -y
sudo npm install npm --global
sudo npm install -g artillery
sudo artillery -V
sudo artillery run payload/artillery.yml -o result.json

ls -la
ls -la payload

sudo artillery report result.json -o result.html


7z a -mx9 result.7z -mhe -p$zippassword result.json result.html

resulturl=$bloburl/result.7z
sudo azcopy --source result.7z --destination $resulturl --dest-key $storagekey

echo hello2 >> log.txt
